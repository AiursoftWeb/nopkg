using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiursoft.Nopkg.Registry;

public partial class NpmRegistry
{
    private async Task<JsonObject> FilterMetadataAsync(JsonObject metadata,
        IReadOnlyDictionary<string, ArtifactIdentity>? approved, CancellationToken token)
    {
        if (_admission == null) return metadata;
        if (_admission.UsesRegistryPolicy)
        {
            var candidateVersions = ((JsonObject)metadata["versions"]!).Select(v => v.Key);
            var eligible = await _admission.CandidatesAsync(metadata["name"]!.GetValue<string>(), candidateVersions, token);
            return FilterVersions(metadata, eligible);
        }
        if (approved == null) throw new AdmissionUnavailableException();
        var decisions = await _admission.AssessAsync(approved.Values.ToList(), token);
        var allowed = decisions.Where(d => d.Trusted && d.PendingVerification == false)
            .Select(d => d.Dependency.Version).ToHashSet(StringComparer.Ordinal);
        return FilterVersions(metadata, allowed);
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

    private async Task<string?> ExpectedHashAsync(string name, string version, CancellationToken token)
    {
        if (_admission == null) return null;
        if (_admission.UsesRegistryPolicy)
        {
            if (!(await _admission.CandidatesAsync(name, [version], token)).Contains(version)) throw new ArtifactDeniedException();
            return null;
        }
        var approved = await _admission.ApprovedAsync(name, _origin.AbsoluteUri.TrimEnd('/'), token);
        if (!approved.TryGetValue(version, out var artifact)) throw new ArtifactDeniedException();
        await _admission.RequireAsync(artifact, token);
        return artifact.Sha256;
    }

    private void ValidateTarballOrigin(JsonObject dist)
    {
        if (!Uri.TryCreate(dist["tarball"]?.GetValue<string>(), UriKind.Absolute, out var url) ||
            url.Scheme != _origin.Scheme || url.Host != _origin.Host || url.Port != _origin.Port ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidDataException("The tarball must come from the configured registry origin.");
    }

    private async Task VerifyAdmissionAsync(string path, string name, string version, JsonObject dist,
        string? expectedHash, CancellationToken token)
    {
        if (_admission == null) return;
        if (expectedHash == null && !_admission.UsesRegistryPolicy) throw new ArtifactDeniedException();
        var artifact = await ActualArtifactAsync(path, name, version, token);
        if (expectedHash != null && artifact.Sha256 != expectedHash) throw new ArtifactDeniedException();
        // Re-evaluate after download and integrity checks, immediately before committing bytes.
        if (_admission.UsesRegistryPolicy) await _admission.VerifyMirrorAsync(artifact, token);
        else await _admission.RequireAsync(artifact, token);
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
            // A fixed internal policy can independently verify historical bytes against its
            // configured upstream. Keep the original provenance; never reclassify the cache.
            var expected = _admission.UsesRegistryPolicy && recorded != null
                ? actual with { Source = recorded.Source }
                : actual;
            if (recorded != expected) throw new ArtifactDeniedException();
            await _admission.RequireAsync(expected, token);
        }
        else
        {
            // The protocol preview predates provenance. Adoption requires an independently
            // verified exact artifact, compatible original origin, and the package's real identity.
            ValidateTarballOrigin(saved);
            await VerifyIdentityAsync(path, name, version, token);
            await _admission.RequireAsync(actual, token);
            saved["_nopkg"] = JsonSerializer.SerializeToNode(actual);
            await AtomicWriteAsync(manifestFile, Encoding.UTF8.GetBytes(saved.ToJsonString()), CancellationToken.None);
        }
    }
}
