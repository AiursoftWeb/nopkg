using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiursoft.Nopkg.Registry;

public partial class NpmRegistry
{
    // Cache file-derived metadata, never admission decisions. Atomic metadata replacement
    // invalidates each entry; deleted files are removed on the next catalog request.
    private sealed record CatalogFile(long Length, DateTime Written, string Name, string[] Versions,
        IReadOnlyDictionary<string, string[]> Dependencies);
    private readonly ConcurrentDictionary<string, CatalogFile> _catalog = new(StringComparer.Ordinal);

    private async Task<IReadOnlyList<(string Path, CatalogFile File)>> CatalogFilesAsync(CancellationToken token)
    {
        var result = new List<(string Path, CatalogFile File)>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(_root))
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, "metadata.json");
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                paths.Add(path);
                if (!_catalog.TryGetValue(path, out var entry) || entry.Length != info.Length || entry.Written != info.LastWriteTimeUtc)
                {
                    await using var input = File.OpenRead(path);
                    using var doc = await JsonDocument.ParseAsync(input, cancellationToken: token);
                    var root = doc.RootElement;
                    var name = PackageName(root.GetProperty("name").GetString()!);
                    var versions = root.GetProperty("versions").EnumerateObject().Select(v => v.Name)
                        .OrderByDescending(v => v, NpmVersionComparer.Instance).ToArray();
                    if (versions.Any(v => v.Length > 128 || !VersionPattern().IsMatch(v)))
                        throw new InvalidDataException("Invalid package version in metadata.");
                    var dependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var version in root.GetProperty("versions").EnumerateObject())
                    foreach (var kind in new[] { "dependencies", "peerDependencies", "optionalDependencies" })
                    {
                        if (!version.Value.TryGetProperty(kind, out var group) || group.ValueKind != JsonValueKind.Object) continue;
                        foreach (var dependency in group.EnumerateObject())
                        {
                            if (!dependencies.TryGetValue(dependency.Name, out var matches)) dependencies[dependency.Name] = matches = [];
                            matches.Add(version.Name);
                        }
                    }
                    // Prefer the published latest tag, then fall back to semantic version order.
                    if (root.TryGetProperty("dist-tags", out var tags) && tags.TryGetProperty("latest", out var tag) &&
                        tag.ValueKind == JsonValueKind.String && versions.Contains(tag.GetString()))
                        versions = versions.Prepend(tag.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                    entry = new CatalogFile(info.Length, info.LastWriteTimeUtc, name, versions,
                        dependencies.ToDictionary(x => x.Key, x => x.Value.Distinct().OrderByDescending(v => v, NpmVersionComparer.Instance).ToArray()));
                    _catalog[path] = entry;
                }
                result.Add((path, entry));
            }
        foreach (var path in _catalog.Keys.Where(p => !paths.Contains(p))) _catalog.TryRemove(path, out _);
        return result;
    }

    private async Task<Dictionary<string, string>> FirstAllowedAsync(IReadOnlyList<(string Name, string[] Versions)> packages, CancellationToken token)
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = packages.Where(p => p.Versions.Length > 0).ToList();
        // One current candidate per family. A family with an approved latest version
        // does not need thousands of historical policy checks just to appear in search.
        var current = pending.Select(p => new MirrorCandidate("npm", p.Name, p.Versions[0], PolicySource)).ToList();
        var admitted = _admission == null ? current : await _admission.CandidatesAsync(current, token);
        foreach (var candidate in admitted) selected[candidate.Name] = candidate.Version;
        // Check older versions in batches, not one HTTP request per rejected version.
        var remaining = pending.Where(p => !selected.ContainsKey(p.Name))
            .SelectMany(p => p.Versions.Skip(1).Select(v => new MirrorCandidate("npm", p.Name, v, PolicySource)));
        foreach (var batch in remaining.Chunk(500))
        {
            var candidates = batch.Where(c => !selected.ContainsKey(c.Name)).ToList();
            if (candidates.Count == 0) continue;
            var allowed = _admission == null ? candidates : await _admission.CandidatesAsync(candidates, token);
            foreach (var candidate in allowed) selected.TryAdd(candidate.Name, candidate.Version);
        }
        return selected;
    }

    public async Task<IReadOnlyList<CachedPackage>> CachedPackagesAsync(CancellationToken token = default, string query = "")
    {
        var local = localPackages == null ? [] : await localPackages.ListAsync(token);
        var names = local.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var files = (await CatalogFilesAsync(token)).Where(p => !names.Contains(p.File.Name) &&
            p.File.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var allowed = await FirstAllowedAsync(files.Select(p => (p.File.Name, p.File.Versions)).ToList(), token);
        var result = files.Where(p => allowed.ContainsKey(p.File.Name)).Select(p =>
            new CachedPackage(p.File.Name) { Version = allowed[p.File.Name] }).ToList();
        result.AddRange(local.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
        return result.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<CachedPackage> DescribeAsync(CachedPackage package, CancellationToken token)
    {
        var metadata = await ReadCatalogMetadataAsync(package.Name, token);
        var versions = metadata["versions"]!.AsObject();
        var version = string.IsNullOrEmpty(package.Version)
            ? metadata["dist-tags"]?["latest"]?.GetValue<string>() ?? versions.Select(v => v.Key).OrderByDescending(v => v, NpmVersionComparer.Instance).FirstOrDefault()
            : package.Version;
        var manifest = version == null ? null : versions[version];
        var author = manifest?["author"];
        return package with
        {
            Version = version ?? "", Description = manifest?["description"]?.GetValue<string>() ?? metadata["description"]?.GetValue<string>() ?? "",
            Author = author is JsonObject person ? person["name"]?.GetValue<string>() ?? "" : author is JsonValue text && text.TryGetValue<string>(out var value) ? value : "",
            Published = DateTimeOffset.TryParse(metadata["time"]?[version ?? ""]?.GetValue<string>(), out var date) ? date : null
        };
    }

    private async Task<JsonObject> ReadCatalogMetadataAsync(string name, CancellationToken token)
    {
        PackageName(name);
        if (localPackages != null && await localPackages.FindAsync(name, token) is { } local) return local.Metadata;
        var file = Path.Combine(PackageDirectory(name), "metadata.json");
        if (!File.Exists(file)) throw new FileNotFoundException("Package is not cached.");
        return JsonNode.Parse(await File.ReadAllBytesAsync(file, token))!.AsObject();
    }

    public async Task<JsonObject> CatalogMetadataAsync(string name, CancellationToken token)
    {
        if (localPackages != null && await localPackages.FindAsync(PackageName(name), token) is { } local) return local.Metadata;
        // Browsing never downloads upstream metadata or package archives.
        return await FilterMetadataAsync(await ReadCatalogMetadataAsync(name, token), token);
    }

    public async Task<string> CachedReadmeAsync(string name, string version, CancellationToken token)
    {
        PackageName(name);
        if (!VersionPattern().IsMatch(version)) throw new ArgumentException("Invalid version.");
        var local = localPackages == null ? null : await localPackages.FindAsync(name, token);
        var path = local != null
            ? local.ArchiveHashes.TryGetValue(version, out var hash) ? PublishedArchivePath(hash) : ""
            : Path.Combine(PackageDirectory(name), version + ".tgz");
        if (!File.Exists(path)) return "";
        // Read existing local bytes only. Bound decompression and never extract archive paths.
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        long scanned = 0;
        for (var count = 0; count < 4096 && await reader.GetNextEntryAsync(cancellationToken: token) is { } entry; count++)
        {
            scanned += entry.Length;
            if (scanned > 32 * 1024 * 1024) return "";
            var relative = entry.Name.StartsWith("package/", StringComparison.Ordinal) ? entry.Name[8..] : entry.Name;
            if (relative.Contains('/') || entry.Length > 1024 * 1024 || entry.DataStream == null ||
                !new[] { "readme", "readme.md", "readme.markdown", "readme.txt" }.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            using var text = new StreamReader(entry.DataStream);
            return await text.ReadToEndAsync(token);
        }
        return "";
    }

    public async Task<IReadOnlyList<CachedPackage>> DependentsAsync(string name, CancellationToken token)
    {
        var local = localPackages == null ? [] : await localPackages.ListAsync(token);
        var names = local.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var files = (await CatalogFilesAsync(token)).Where(p => !names.Contains(p.File.Name) && p.File.Dependencies.ContainsKey(name)).ToList();
        var allowed = await FirstAllowedAsync(files.Select(p => (p.File.Name, p.File.Dependencies[name])).ToList(), token);
        var result = files.Where(p => allowed.ContainsKey(p.File.Name)).Select(p =>
            new CachedPackage(p.File.Name) { Version = allowed[p.File.Name] }).ToList();
        foreach (var package in local)
        {
            var metadata = await ReadCatalogMetadataAsync(package.Name, token);
            var versions = metadata["versions"]!.AsObject().Where(v => new[] { "dependencies", "peerDependencies", "optionalDependencies" }
                .Any(kind => v.Value?[kind] is JsonObject group && group.ContainsKey(name)))
                .Select(v => v.Key).OrderByDescending(v => v, NpmVersionComparer.Instance).ToList();
            if (versions.Count > 0) result.Add(package with { Version = versions[0] });
        }
        return result.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }
}
