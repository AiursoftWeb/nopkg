using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Registry;

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
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            await context.Response.WriteAsJsonAsync(new { error = "This registry preview is read-only." });
            return;
        }
        try
        {
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
        catch (Exception ex) when (ex is ArtifactDeniedException or AdmissionUnavailableException or ArgumentException or FileNotFoundException or InvalidDataException or HttpRequestException or HttpIOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            context.Response.StatusCode = ex switch
            {
                ArtifactDeniedException => StatusCodes.Status403Forbidden,
                AdmissionUnavailableException => StatusCodes.Status503ServiceUnavailable,
                ArgumentException => StatusCodes.Status400BadRequest,
                FileNotFoundException => StatusCodes.Status404NotFound,
                OperationCanceledException => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status502BadGateway
            };
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
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
