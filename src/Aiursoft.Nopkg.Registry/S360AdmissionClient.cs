using System.Net.Http.Json;
using System.Text.Json;

namespace Aiursoft.Nopkg.Registry;

public class S360AdmissionOptions
{
    public bool Enabled { get; set; }
    public string PolicySource { get; set; } = "";
    public string Server { get; set; } = "";
    public string CheckKey { get; set; } = "";
}

public record ArtifactIdentity(string Ecosystem, string Name, string Version, string Source, string Sha256);
public record MirrorCandidate(string Ecosystem, string Name, string Version, string Source);
public record ArtifactDecision(ArtifactIdentity Dependency, bool Trusted, bool? PendingVerification);
public record ArtifactAssessmentResponse(List<ArtifactDecision?>? Decisions);

public class AdmissionUnavailableException() : Exception("Package admission is unavailable. Please try again later.");
public class ArtifactDeniedException() : Exception("This package version is not admitted by S360.");

/// <summary>Queries compliance only; package downloads and integrity checks belong to the registry.</summary>
public class S360AdmissionClient
{
    public bool UsesRegistryPolicy { get; }
    public string PolicySource { get; }
    private readonly HttpClient _client;
    private readonly Uri _server;
    private readonly string _checkKey;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public S360AdmissionClient(HttpClient client, S360AdmissionOptions options)
    {
        _client = client;
        UsesRegistryPolicy = !string.IsNullOrEmpty(options.PolicySource);
        if (UsesRegistryPolicy && (!Uri.TryCreate(options.PolicySource, UriKind.Absolute, out var policy) ||
            policy.Scheme != "https" || !string.IsNullOrEmpty(policy.UserInfo) ||
            !string.IsNullOrEmpty(policy.Query) || !string.IsNullOrEmpty(policy.Fragment)))
            throw new ArgumentException("S360 PolicySource must be the internal HTTPS npm registry URL.");
        PolicySource = options.PolicySource.TrimEnd('/');
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
            var result = await SendAsync<ArtifactAssessmentResponse>(HttpMethod.Post, "api/artifacts/assess", new { dependencies = batch.Select(a => new { a.Ecosystem, a.Name, a.Version, a.Source }) }, token);
            if (result.Decisions == null || result.Decisions.Count != batch.Length ||
                result.Decisions.Where((d, i) => d?.Dependency is not { } dependency || dependency with { Sha256 = batch[i].Sha256 } != batch[i] || d.PendingVerification == null).Any())
                throw new AdmissionUnavailableException();
            if (result.Decisions.Any(d => d!.PendingVerification == true)) throw new AdmissionUnavailableException();
            decisions.AddRange(result.Decisions.Select((d, i) => d! with { Dependency = batch[i] }));
        }
        return decisions;
    }

    public async Task<HashSet<string>> CandidatesAsync(string name, IEnumerable<string> versions, CancellationToken token)
    {
        var candidates = versions.Distinct(StringComparer.Ordinal)
            .Select(v => new MirrorCandidate("npm", name, v, PolicySource)).ToArray();
        return (await CandidatesAsync(candidates, token)).Select(c => c.Version).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<MirrorCandidate>> CandidatesAsync(IReadOnlyList<MirrorCandidate> candidates, CancellationToken token)
    {
        var allowed = new List<MirrorCandidate>();
        foreach (var batch in candidates.Distinct().Chunk(500))
        {
            var decisions = await AssessAsync(batch.Select(c =>
                new ArtifactIdentity(c.Ecosystem, c.Name, c.Version, c.Source, "")).ToList(), token);
            allowed.AddRange(decisions.Where(d => d.Trusted).Select(d =>
                new MirrorCandidate(d.Dependency.Ecosystem, d.Dependency.Name, d.Dependency.Version, d.Dependency.Source)));
        }
        return allowed;
    }

    public async Task RequireAsync(ArtifactIdentity artifact, CancellationToken token)
    {
        var requested = UsesRegistryPolicy ? artifact with { Source = PolicySource } : artifact;
        var decision = (await AssessAsync([requested], token))[0];
        if (!decision.Trusted) throw new ArtifactDeniedException();
    }
}
