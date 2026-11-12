using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Registry;
using Aiursoft.Nopkg.Services.Authentication;
using Aiursoft.Nopkg.Services.Publishing;
using Microsoft.AspNetCore.Http.Features;

namespace Aiursoft.Nopkg.Services.Registry;

public static class RegistryEndpoint
{
    public static void MapNpmRegistry(this WebApplication app) => app.Map("/npm", branch => branch.Run(HandleAsync));

    private static async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var path = Uri.UnescapeDataString(context.Request.Path.Value ?? "/").TrimEnd('/');
        if (path.StartsWith("/-/npm/v1/security/", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            await context.Response.WriteAsJsonAsync(new { error = "Npm audit is not implemented in this protocol preview." });
            return;
        }
        try
        {
            var authentication = context.RequestServices.GetRequiredService<PackagePublisherAuthenticationService>();
            var key = context.Request.Headers.Authorization.ToString();
            key = key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key[7..].Trim() : "";
            if (path == "/-/whoami")
            {
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                    throw new PublishingException(405, "Use GET or HEAD for account information.");
                var publisher = await authentication.AuthenticateAsync(key, context.RequestAborted) ?? throw new PublishingException(401, "A valid personal API key is required.");
                await WriteJsonAsync(context, new JsonObject { ["username"] = publisher.UserName ?? publisher.UserId });return;
            }
            if (path.StartsWith("/-/package/", StringComparison.Ordinal))
            {
                var parts = path["/-/package/".Length..].Split("/dist-tags", 2);
                if (parts.Length != 2 || (parts[1].Length > 0 && !parts[1].StartsWith('/'))) throw new FileNotFoundException("Tag endpoint not found.");
                var service = context.RequestServices.GetRequiredService<PackagePublishingService>();
                if (HttpMethods.IsGet(context.Request.Method) && parts[1].Length == 0)
                {
                    var tagMetadata = await context.RequestServices.GetRequiredService<NpmRegistry>().MetadataAsync(parts[0], context.RequestAborted);
                    await WriteJsonAsync(context, tagMetadata["dist-tags"] ?? new JsonObject());return;
                }
                if (parts[1].Length < 2) throw new PublishingException(405, "Specify a distribution tag.");
                string? version = null;
                if (HttpMethods.IsPut(context.Request.Method))
                {
                    await RequireWriterAsync(context, key);
                    var value = await ReadJsonAsync(context, 1024);
                    if (value is not JsonValue stringValue || !stringValue.TryGetValue(out version) || string.IsNullOrEmpty(version))
                        throw new PublishingException(400, "A version string is required.");
                }
                else if (!HttpMethods.IsDelete(context.Request.Method)) throw new PublishingException(405, "Use PUT or DELETE for a distribution tag.");
                await service.ChangeTagAsync(parts[0], parts[1][1..], version, key, context.RequestAborted);
                await context.Response.WriteAsJsonAsync(new { ok = true });return;
            }
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                if (!HttpMethods.IsPut(context.Request.Method) || path.StartsWith("/-/", StringComparison.Ordinal))
                    throw new PublishingException(405, "Use npm publish or npm dist-tag. Package deletion is not supported.");
                await RequireWriterAsync(context, key);
                var options = context.RequestServices.GetRequiredService<RegistryOptions>();
                var maximum = Math.Min(options.MaxArchiveBytes, options.MaxPublishArchiveBytes) * 4 / 3 + 4 * 1024 * 1024;
                var body = await ReadJsonAsync(context, maximum) as JsonObject ?? throw new PublishingException(400, "Invalid publication document.");
                await context.RequestServices.GetRequiredService<PackagePublishingService>().PublishAsync(path.TrimStart('/'), body, key, context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status201Created;
                await context.Response.WriteAsJsonAsync(new { ok = true, id = path.TrimStart('/') });return;
            }
            if (path == "/-/ping")
            {
                await WriteJsonAsync(context, new JsonObject { ["registry"] = "Aiursoft Nopkg" });
                return;
            }
            var registry = context.RequestServices.GetRequiredService<NpmRegistry>();
            const string archivePrefix = "/-/tarballs/";
            if (path.StartsWith(archivePrefix, StringComparison.Ordinal))
            {
                var relative = path[archivePrefix.Length..];
                var split = relative.LastIndexOf('/');
                if (split < 1 || !relative.EndsWith(".tgz", StringComparison.Ordinal))
                    throw new FileNotFoundException("Tarball not found.");
                var name = relative[..split];
                var version = relative[(split + 1)..^4];
                var file = await registry.ArchiveAsync(name, version, context.RequestAborted);
                await Results.File(file, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
                return;
            }
            var components = path.TrimStart('/').Split('/');
            var scoped = components[0].StartsWith('@');
            var packageSegments = scoped ? 2 : 1;
            // npm lockfiles retain the standard registry tarball layout when the registry host changes.
            if (components.Length == packageSegments + 2 && components[packageSegments] == "-")
            {
                var package = string.Join('/', components.Take(packageSegments));
                var prefix = components[packageSegments - 1] + "-";
                var filename = components[^1];
                if (!filename.StartsWith(prefix, StringComparison.Ordinal) || !filename.EndsWith(".tgz", StringComparison.Ordinal) || filename.Length <= prefix.Length + 4)
                    throw new FileNotFoundException("Tarball not found.");
                var version = filename[prefix.Length..^4];
                // Use the same admission, ownership and integrity checks as canonical download URLs.
                var file = await registry.ArchiveAsync(package, version, context.RequestAborted);
                await Results.File(file, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
                return;
            }
            if (components.Length < packageSegments || components.Length > packageSegments + 1)
                throw new FileNotFoundException("Registry endpoint not found.");
            var packageName = string.Join('/', components.Take(packageSegments));
            var metadata = await registry.MetadataAsync(packageName, context.RequestAborted);
            var url = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
            var rewritten = NpmRegistry.RewriteMetadata(metadata, url);
            JsonNode result = rewritten;
            if (components.Length == packageSegments + 1)
            {
                var selector = components[^1];
                var version = rewritten["dist-tags"]?[selector]?.GetValue<string>() ?? selector;
                result = rewritten["versions"]?[version] ?? throw new FileNotFoundException("Package version not found.");
            }
            await WriteJsonAsync(context, result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex) when (ex is PublishingException or BadHttpRequestException or ArtifactDeniedException or AdmissionUnavailableException or ArgumentException or FileNotFoundException or InvalidDataException or HttpRequestException or HttpIOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            context.Response.StatusCode = ex switch
            {
                PublishingException publishing => publishing.Status,
                BadHttpRequestException bad => bad.StatusCode,
                ArtifactDeniedException => StatusCodes.Status403Forbidden,
                AdmissionUnavailableException => StatusCodes.Status503ServiceUnavailable,
                ArgumentException => StatusCodes.Status400BadRequest,
                FileNotFoundException => StatusCodes.Status404NotFound,
                OperationCanceledException => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status502BadGateway
            };
            if (context.Response.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }

    private static async Task RequireWriterAsync(HttpContext context, string key)
    {
        var publisher = await context.RequestServices.GetRequiredService<PackagePublisherAuthenticationService>()
            .AuthenticateAsync(key, context.RequestAborted) ?? throw new PublishingException(401, "A valid personal API key is required.");
        if (!publisher.CanPublish && !publisher.CanManagePackages) throw new PublishingException(403, "Publishing permission is required.");
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpContext context, long maximum)
    {
        SetBodyLimit(context, maximum);
        using var input = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            if (input.Length + count > maximum) throw new PublishingException(413, "The request exceeds the configured size limit.");
            await input.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
        }
        try { return JsonNode.Parse(input.GetBuffer().AsSpan(0, checked((int)input.Length))); }
        catch (System.Text.Json.JsonException) { throw new PublishingException(400, "Invalid JSON request."); }
    }

    private static void SetBodyLimit(HttpContext context, long maximum)
    {
        if (context.Request.ContentLength > maximum) throw new PublishingException(413, "The request exceeds the configured size limit.");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maximum;
    }

    private static async Task WriteJsonAsync(HttpContext context, JsonNode value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\"";
        context.Response.Headers.ETag = etag;
        if (context.Request.Headers.IfNoneMatch.ToString().Split(',').Any(s => s.Trim().TrimStart('W', '/') == etag || s.Trim() == "*"))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }
}
