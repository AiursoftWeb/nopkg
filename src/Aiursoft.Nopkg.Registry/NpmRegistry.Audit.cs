using System.Text;
using System.Text.Json.Nodes;

namespace Aiursoft.Nopkg.Registry;

public partial class NpmRegistry
{
    public async Task<JsonObject> AuditAsync(JsonObject packages, CancellationToken token = default)
    {
        foreach (var (name, versions) in packages)
        {
            PackageName(name);
            if (versions is not JsonArray array || array.Any(version =>
                    version is not JsonValue value || !value.TryGetValue<string>(out var text) ||
                    string.IsNullOrWhiteSpace(text) || text.Length > 256))
                throw new ArgumentException("Audit packages must contain arrays of version strings.");
        }
        if (options.CacheOnly)
            throw new HttpRequestException("Fresh vulnerability information is unavailable in cache-only mode.");

        // Use the configured registry and a new request: never forward a publisher's credentials.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_origin, "-/npm/v1/security/advisories/bulk"));
        request.Headers.Accept.ParseAdd("application/json");
        // Some registry proxies cannot relay chunked audit bodies. The bounded JSON has a known length.
        request.Content = new StringContent(packages.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        await CopyLimitedAsync(input, output, 32 * 1024 * 1024, token);
        var result = JsonNode.Parse(output.GetBuffer().AsSpan(0, checked((int)output.Length))) as JsonObject;
        if (result == null || result.Any(item => item.Value is not JsonArray))
            throw new InvalidDataException("The upstream returned an invalid bulk advisory response.");
        return result;
    }
}
