using System.Net.Http.Json;
using System.Text.Json;

namespace Aiursoft.Nopkg.Registry;

public class S360AdmissionOptions
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "";
    public string CheckKey { get; set; } = "";
}

public record ArtifactIdentity(string Ecosystem, string Name, string Version, string Source, string Sha256);
public record ArtifactDecision(ArtifactIdentity Dependency, bool Trusted, bool? PendingVerification);
public record ArtifactAssessmentResponse(List<ArtifactDecision?>? Decisions);
public record ApprovedArtifactPage(List<ArtifactIdentity?>? Artifacts, int? NextAfterId, int UpperId);

public class AdmissionUnavailableException() : Exception("Package admission is unavailable. Please try again later.");
public class ArtifactDeniedException() : Exception("This package version is not admitted by S360.");

/// <summary>Read-only admission. Registry requests never approve or submit dependencies.</summary>
public class S360AdmissionClient
{
    private readonly HttpClient _client;
    private readonly Uri _server;
    private readonly string _checkKey;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public S360AdmissionClient(HttpClient client, S360AdmissionOptions options)
    {
        _client = client;
        if (!Uri.TryCreate(options.Server.TrimEnd('/') + "/", UriKind.Absolute, out var server) ||
            (server.Scheme != "https" && !(server.Scheme == "http" && server.IsLoopback)) ||
            !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) ||
            !string.IsNullOrEmpty(server.Fragment) || string.IsNullOrWhiteSpace(options.CheckKey) ||
            options.CheckKey.Any(char.IsControl))
            throw new ArgumentException("S360 admission requires an HTTPS server and a check key.");
        _server = server;
        _checkKey = options.CheckKey;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(_server, path));
            request.Headers.Add("X-S360-Key", _checkKey);
            if (body != null) request.Content = JsonContent.Create(body);
            using var response = await _client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, token)
                ?? throw new AdmissionUnavailableException();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or NotSupportedException)
        {
            // Do not expose the key, policy response body, or upstream URL to registry clients.
            throw new AdmissionUnavailableException();
        }
    }

    public async Task<IReadOnlyList<ArtifactDecision>> AssessAsync(IReadOnlyList<ArtifactIdentity> artifacts, CancellationToken token)
    {
        var decisions = new List<ArtifactDecision>();
        foreach (var batch in artifacts.Chunk(500))
        {
            var result = await SendAsync<ArtifactAssessmentResponse>(HttpMethod.Post, "api/artifacts/assess", new { dependencies = batch }, token);
            if (result.Decisions == null || result.Decisions.Count != batch.Length ||
                result.Decisions.Where((d, i) => d == null || d.Dependency != batch[i] || d.PendingVerification == null).Any())
                throw new AdmissionUnavailableException();
            decisions.AddRange(result.Decisions.Select(d => d!));
        }
        return decisions;
    }

    public async Task<IReadOnlyDictionary<string, ArtifactIdentity>> ApprovedAsync(string name, string source, CancellationToken token)
    {
        var artifacts = new List<ArtifactIdentity>();
        var after = 0;
        int? upper = null;
        do
        {
            var path = $"api/artifacts/approved?ecosystem=npm&name={Uri.EscapeDataString(name)}&source={Uri.EscapeDataString(source)}&afterId={after}&take=100";
            if (upper.HasValue) path += $"&upperId={upper.Value}";
            var page = await SendAsync<ApprovedArtifactPage>(HttpMethod.Get, path, null, token);
            if (page.Artifacts == null || page.Artifacts.Count > 100 || page.UpperId < after ||
                (upper.HasValue && page.UpperId != upper.Value) ||
                (page.NextAfterId.HasValue && (page.NextAfterId <= after || page.NextAfterId > page.UpperId)))
                throw new AdmissionUnavailableException();
            upper = page.UpperId;
            foreach (var artifact in page.Artifacts)
            {
                if (artifact == null || artifact.Ecosystem != "npm" || artifact.Name != name || artifact.Source != source ||
                    string.IsNullOrWhiteSpace(artifact.Version) || artifact.Version.Length > 128 ||
                    artifact.Sha256 is not { Length: 64 } || !artifact.Sha256.All(Uri.IsHexDigit))
                    throw new AdmissionUnavailableException();
                artifacts.Add(artifact with { Sha256 = artifact.Sha256.ToLowerInvariant() });
            }
            if (!page.NextAfterId.HasValue) break;
            after = page.NextAfterId.Value;
        } while (true);

        // Conflicting raw artifacts for one version cannot be represented by one npm version.
        return artifacts.Distinct().GroupBy(a => a.Version, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    public async Task RequireAsync(ArtifactIdentity artifact, CancellationToken token)
    {
        var decisions = await AssessAsync([artifact], token);
        if (!decisions[0].Trusted || decisions[0].PendingVerification != false) throw new ArtifactDeniedException();
    }
}
