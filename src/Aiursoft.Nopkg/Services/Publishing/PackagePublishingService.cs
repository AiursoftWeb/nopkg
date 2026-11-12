using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Registry;
using Aiursoft.Nopkg.Services.Authentication;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;
namespace Aiursoft.Nopkg.Services.Publishing;

public sealed class PackagePublishingService(NopkgDbContext db, NpmRegistry registry,
    RegistryOptions options, PackagePublisherAuthenticationService authentication)
{
    public async Task PublishAsync(string name, JsonObject document, string rawKey, CancellationToken token)
    {
        NpmRegistry.PackageName(name);
        await RequireAsync(rawKey, token);
        if (Text(document["name"]) != name || document["versions"] is not JsonObject versions || versions.Count != 1 ||
            document["_attachments"] is not JsonObject attachments || attachments.Count != 1)
            throw new PublishingException(400, "Publish exactly one package version and one tarball attachment.");
        var version = versions.First().Key;
        if (versions.First().Value is not JsonObject supplied || Text(supplied["name"]) != name || Text(supplied["version"]) != version)
            throw new PublishingException(400, "Publication metadata has an inconsistent package identity.");
        var attachment = attachments.First().Value as JsonObject ?? throw new PublishingException(400, "Missing tarball attachment.");
        var encoded = Text(attachment["data"]) ?? throw new PublishingException(400, "Missing tarball bytes.");
        var maximum = Math.Min(options.MaxArchiveBytes, options.MaxPublishArchiveBytes);
        if (encoded.Length > maximum * 4 / 3 + 8) throw new PublishingException(413, "Published archive exceeds the configured size limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new PublishingException(400, "Invalid tarball encoding."); }
        if (bytes.LongLength > maximum) throw new PublishingException(413, "Published archive exceeds the configured size limit.");
        if (attachment["length"] is { } lengthNode &&
            (lengthNode is not JsonValue lengthValue || !lengthValue.TryGetValue<long>(out var length) || length != bytes.LongLength))
            throw new PublishingException(400, "Tarball length does not match the attachment.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = registry.PublishedArchivePath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            JsonObject manifest;
            try { manifest = await registry.ValidatePublishedArchiveAsync(temporary, name, version, token); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or System.Text.Json.JsonException or OverflowException)
            { throw new PublishingException(400, "The package archive is invalid or does not match the submitted identity."); }
            // Registry fields come from the actual archive, not the separately submitted manifest.
            manifest["dist"] = new JsonObject { ["tarball"] = "/-/tarballs/" + Uri.EscapeDataString(name) + "/" + version + ".tgz",
                ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(bytes)),
                ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes)) };
            manifest.Remove("_attachments");
            var publisher = await RequireAsync(rawKey, token);
            var family = await db.PackageFamilies.NotCacheable().SingleOrDefaultAsync(x => x.Name == name, token);
            if (family == null)
            {
                // Existing preview caches predate relational ownership. They cannot be claimed by uploading.
                if (await registry.HasCachedMirrorAsync(name))
                    throw new PublishingException(403, "A mirrored package requires an administrator to assign ownership first.");
                family = new PackageFamily { Name = name, IsLocallyPublished = true, OwnerUserId = publisher.UserId };
                db.PackageFamilies.Add(family);
            }
            else if (!family.IsLocallyPublished || (!publisher.CanManagePackages && family.OwnerUserId != publisher.UserId))
                throw new PublishingException(403, "You do not own this internally published package.");
            if (family.Id != 0 && await db.PublishedPackages.NotCacheable().AnyAsync(x => x.FamilyId == family.Id && x.Version == version, token))
                throw new PublishingException(409, "This version is already published and cannot be overwritten.");
            var tags = JsonNode.Parse(family.TagsJson) as JsonObject ?? new JsonObject();
            if (document["dist-tags"] is JsonObject updates)
                foreach (var (tag, value) in updates)
                {
                    ValidateTag(tag);
                    if (Text(value) != version) throw new PublishingException(400, "A publish tag must point to the uploaded version.");
                    tags[tag] = version;
                }
            family.TagsJson = tags.ToJsonString();
            family.Revision = Guid.NewGuid().ToString();
            db.PublishedPackages.Add(new PublishedPackage { Family = family, Version = version, Sha256 = hash,
                ManifestJson = manifest.ToJsonString(), UploadedByUserId = publisher.UserId });
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                await using var existing = File.OpenRead(path);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, token)) != hash)
                    throw new InvalidDataException("Published storage contains conflicting bytes.");
            }
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException) { throw new PublishingException(409, "Package ownership or version changed. Review the current package and retry."); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task ChangeTagAsync(string name, string tag, string? version, string rawKey, CancellationToken token)
    {
        NpmRegistry.PackageName(name);ValidateTag(tag);
        var publisher = await RequireAsync(rawKey, token);
        var family = await db.PackageFamilies.NotCacheable().SingleOrDefaultAsync(x => x.Name == name, token);
        if (family is not { IsLocallyPublished: true }) throw new PublishingException(404, "Internally published package not found.");
        if (!publisher.CanManagePackages && family.OwnerUserId != publisher.UserId) throw new PublishingException(403, "You do not own this package.");
        if (version != null && !await db.PublishedPackages.NotCacheable().AnyAsync(x => x.FamilyId == family.Id && x.Version == version, token))
            throw new PublishingException(404, "Published version not found.");
        var tags = JsonNode.Parse(family.TagsJson) as JsonObject ?? new JsonObject();
        if (version == null) tags.Remove(tag);else tags[tag] = version;
        family.TagsJson = tags.ToJsonString();family.Revision = Guid.NewGuid().ToString();
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw new PublishingException(409, "Package tags changed. Retry after reading the current tags."); }
    }

    private async Task<PackagePublisher> RequireAsync(string rawKey, CancellationToken token)
    {
        var publisher = await authentication.AuthenticateAsync(rawKey, token) ?? throw new PublishingException(401, "A valid personal API key is required.");
        if (!publisher.CanPublish && !publisher.CanManagePackages) throw new PublishingException(403, "Publishing permission is required.");
        return publisher;
    }

    private static string? Text(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new PublishingException(400, "A text field has an invalid JSON type.");
    }

    private static void ValidateTag(string tag)
    {
        if (tag.Length is 0 or > 128 || !char.IsAsciiLetter(tag[0]) || tag.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_' and not '.'))
            throw new PublishingException(400, "A distribution tag must start with a letter and contain only letters, digits, dots, hyphens or underscores.");
    }
}
