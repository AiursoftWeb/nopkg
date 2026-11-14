using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiursoft.Nopkg.Registry;

public partial class NpmRegistry
{
    private string PolicySource => _admission is { UsesRegistryPolicy: true }
        ? _admission.PolicySource : _origin.AbsoluteUri.TrimEnd('/');

    private async Task<JsonObject> FilterMetadataAsync(JsonObject metadata, CancellationToken token)
    {
        if (_admission == null) return metadata;
        var candidates = ((JsonObject)metadata["versions"]!).Select(v =>
            new MirrorCandidate("npm", metadata["name"]!.GetValue<string>(), v.Key, PolicySource)).ToList();
        var eligible = (await _admission.CandidatesAsync(candidates, token))
            .Select(c => c.Version).ToHashSet(StringComparer.Ordinal);
        if (eligible.Count == 0) throw new FileNotFoundException("No package version is admitted by S360.");
        return FilterVersions(metadata, eligible);
    }

    private static JsonObject FilterVersions(JsonObject metadata, HashSet<string> allowed)
    {
        var result = (JsonObject)metadata.DeepClone();
        var versions = (JsonObject)result["versions"]!;
        foreach (var version in versions.Select(v => v.Key).Where(v => !allowed.Contains(v)).ToList())
            versions.Remove(version);
        if (result["dist-tags"] is JsonObject tags)
            foreach (var tag in tags.ToList())
                if (tag.Value is not JsonValue value || !value.TryGetValue<string>(out var version) || !versions.ContainsKey(version))
                    tags.Remove(tag.Key);
        if (result["time"] is JsonObject times)
            foreach (var version in times.Select(v => v.Key).Where(v => v is not ("created" or "modified") && !versions.ContainsKey(v)).ToList())
                times.Remove(version);
        return result;
    }

    private async Task RequireVersionAsync(string name, string version, CancellationToken token)
    {
        if (_admission != null)
            await _admission.RequireAsync(new ArtifactIdentity("npm", name, version, PolicySource, ""), token);
    }

    private void ValidateTarballOrigin(JsonObject dist)
    {
        if (!Uri.TryCreate(dist["tarball"]?.GetValue<string>(), UriKind.Absolute, out var url) ||
            url.Scheme != _origin.Scheme || url.Host != _origin.Host || url.Port != _origin.Port ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidDataException("The tarball must come from the configured registry origin.");
    }

    private async Task VerifyAdmissionAsync(string path, string name, string version, JsonObject dist,
        CancellationToken token)
    {
        if (_admission == null) return;
        var artifact = await ActualArtifactAsync(path, name, version, token);
        // Re-evaluate after download and integrity checks, immediately before committing bytes.
        await _admission.RequireAsync(artifact, token);
        dist["_nopkg"] = JsonSerializer.SerializeToNode(artifact);
    }

    private async Task<ArtifactIdentity> ActualArtifactAsync(string path, string name, string version, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
        return new ArtifactIdentity("npm", name, version, _origin.AbsoluteUri.TrimEnd('/'), hash);
    }

    private async Task AdmitArchiveAsync(string path, string name, string version, JsonObject saved,
        string manifestFile, CancellationToken token)
    {
        if (_admission == null) return;
        var actual = await ActualArtifactAsync(path, name, version, token);
        if (saved["_nopkg"] is { } provenance)
        {
            ArtifactIdentity? recorded;
            try { recorded = provenance.Deserialize<ArtifactIdentity>(); }
            catch (JsonException) { throw new InvalidDataException("Invalid cached artifact provenance."); }
            // Keep the original bytes and provenance across policy-source changes.
            var expected = _admission.UsesRegistryPolicy && recorded != null
                ? actual with { Source = recorded.Source }
                : actual;
            if (recorded != expected) throw new ArtifactDeniedException();
            await _admission.RequireAsync(expected, token);
        }
        else
        {
            // A legacy cache needs valid origin, identity and current compliance.
            ValidateTarballOrigin(saved);
            await VerifyIdentityAsync(path, name, version, token);
            await _admission.RequireAsync(actual, token);
            saved["_nopkg"] = JsonSerializer.SerializeToNode(actual);
            await AtomicWriteAsync(manifestFile, Encoding.UTF8.GetBytes(saved.ToJsonString()), CancellationToken.None);
        }
    }
}
