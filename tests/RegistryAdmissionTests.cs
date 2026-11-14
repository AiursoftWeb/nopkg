using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Registry;

namespace Aiursoft.Nopkg.Tests;

public partial class RegistryTests
{
    private sealed class AdmissionHandler(byte[] archive) : HttpMessageHandler
    {
        public bool Allowed { get; set; } = true;
        public bool Unavailable { get; set; }
        public bool Pending { get; set; }
        public bool MismatchedDecision { get; set; }
        public string Hash { get; set; } = Convert.ToHexStringLower(SHA256.HashData(archive));
        public string Source { get; set; } = "https://registry.example";
        public int Assessments { get; private set; }
        public int Lookups { get; private set; }
        public List<ArtifactIdentity>? CatalogArtifacts { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("s360.example", request.RequestUri!.Host);
            Assert.AreEqual("isolated-fixture-key", request.Headers.GetValues("X-S360-Key").Single());
            if (Unavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var identity = new ArtifactIdentity("npm", "@example/library", "1.0.0", Source, Hash);
            var artifacts = CatalogArtifacts ?? [identity];
            if (request.Method == HttpMethod.Get)
            {
                Lookups++;
                throw new AssertFailedException("Policy checks must not query the retired file-proof catalog.");
            }
            Assert.AreEqual("/api/artifacts/assess", request.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Assert.IsTrue(body["dependencies"]!.AsArray().All(a => !a!.AsObject().ContainsKey("sha256")));
            var identities = body["dependencies"]!.Deserialize<List<ArtifactIdentity>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assessments++;
            return Response(JsonSerializer.SerializeToNode(new ArtifactAssessmentResponse(identities.Select(a =>
                (ArtifactDecision?)new ArtifactDecision(MismatchedDecision ? a with { Name = "different-library" } : a,
                    Allowed && artifacts.Any(p => p.Ecosystem == a.Ecosystem && p.Name == a.Name && p.Version == a.Version && p.Source == a.Source), Pending)).ToList()))!.AsObject());
        }
    }

    [TestMethod]
    public async Task CatalogBatchesCurrentAdmissionWithoutCachingRevokedOrPendingDecisions()
    {
        var archive = Archive();
        foreach (var name in new[] { "catalog-one", "catalog-two" })
        {
            var path = Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))));
            Directory.CreateDirectory(path);
            var metadata = Metadata(archive);
            metadata["name"] = name;
            await File.WriteAllTextAsync(Path.Combine(path, "metadata.json"), metadata.ToJsonString());
        }
        var handler = new AdmissionHandler(archive)
        {
            CatalogArtifacts = [
                new("npm", "catalog-one", "1.0.0", "https://registry.example", new string('a', 64)),
                new("npm", "catalog-two", "1.0.0", "https://registry.example", new string('b', 64))]
        };
        using var policy = new HttpClient(handler);
        using var upstream = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Catalog must not fetch upstream.")));
        var registry = Controlled(upstream, policy);
        Assert.AreEqual(2, (await registry.CachedPackagesAsync()).Count);
        Assert.AreEqual(0, handler.Lookups, "Catalog queries exact observed versions without a file-proof list.");
        Assert.AreEqual(1, handler.Assessments, "Assess the current artifact decisions together.");
        var selected = await registry.CachedPackagesAsync(query: "CATALOG-ONE");
        Assert.AreEqual("catalog-one", selected.Single().Name);
        handler.CatalogArtifacts.Add(handler.CatalogArtifacts[0] with { Sha256 = new string('c', 64) });
        Assert.AreEqual(2, (await registry.CachedPackagesAsync()).Count,
            "Historical hashes do not affect package policy.");
        handler.Pending = true;
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.CachedPackagesAsync());
        handler.Pending = false;
        handler.Allowed = false;
        Assert.AreEqual(0, (await registry.CachedPackagesAsync()).Count);
        handler.Unavailable = true;
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.CachedPackagesAsync());
    }

    [TestMethod]
    public async Task CatalogSearchSkipsNonmatchingMirrorAdmissionEntirely()
    {
        var path = Path.Combine(_directory, "unrelated");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "metadata.json"), Metadata(Archive()).ToJsonString());
        using var upstream = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Catalog must not fetch upstream.")));
        using var policy = new HttpClient(new AdmissionHandler(Archive()) { Unavailable = true });
        var registry = Controlled(upstream, policy);
        Assert.AreEqual(0, (await registry.CachedPackagesAsync(query: "unrelated-search")).Count);
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.CachedPackagesAsync(query: "@EXAMPLE/"));
    }

    private sealed class SinglePolicyHandler : HttpMessageHandler
    {
        public bool Allowed { get; set; } = true;
        public bool Unavailable { get; set; }
        public int CandidateChecks { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("/api/artifacts/assess", request.RequestUri!.AbsolutePath);
            if (Unavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.IsTrue(json["dependencies"]!.AsArray().All(a => !a!.AsObject().ContainsKey("sha256")));
            var identities = json["dependencies"]!.Deserialize<List<ArtifactIdentity>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.IsTrue(identities.All(a => a.Source == "https://nopkg.fixture/npm"));
            CandidateChecks++;
            var result = new ArtifactAssessmentResponse(identities.Select(a =>
                (ArtifactDecision?)new ArtifactDecision(a, Allowed && a.Version == "1.0.0", false)).ToList());
            return Response(JsonSerializer.SerializeToNode(result)!.AsObject());
        }
    }

    [TestMethod]
    public async Task RegistryPolicyCatalogBatchesAcrossPackageNamesAndStillHonorsRevocation()
    {
        var archive = Archive();
        for (var index = 0; index < 501; index++)
        {
            var name = "catalog-" + index;
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(path);
            var metadata = Metadata(archive);
            metadata["name"] = name;
            metadata["versions"]!["1.0.0"]!["name"] = name;
            await File.WriteAllTextAsync(Path.Combine(path, "metadata.json"), metadata.ToJsonString());
        }
        var handler = new SinglePolicyHandler();
        using var policy = new HttpClient(handler);
        using var upstream = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Catalog must not fetch upstream.")));
        var options = Options();
        options.S360 = new S360AdmissionOptions { Enabled = true, Server = "https://s360.example", CheckKey = "fixture", PolicySource = "https://nopkg.fixture/npm" };
        var registry = new NpmRegistry(upstream, options, new S360AdmissionClient(policy, options.S360));
        Assert.AreEqual(501, (await registry.CachedPackagesAsync()).Count);
        Assert.AreEqual(2, handler.CandidateChecks, "The existing 500-candidate API limit applies across package names.");
        handler.Allowed = false;
        Assert.AreEqual(0, (await registry.CachedPackagesAsync()).Count);
        handler.Unavailable = true;
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.CachedPackagesAsync());
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tgz", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task SingleInternalPolicySupportsColdMirroringAndBlocksRevokedCache()
    {
        var bytes = Archive();
        var downloaded = 0;
        using var upstream = new HttpClient(new RegistryHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)) { downloaded++; return Response(bytes); }
            return Response(Metadata(bytes));
        }));
        var handler = new SinglePolicyHandler();
        using var policy = new HttpClient(handler);
        var options = Options();
        options.S360 = new S360AdmissionOptions { Enabled = true, Server = "https://s360.example/", CheckKey = "fixture", PolicySource = "https://nopkg.fixture/npm" };
        var registry = new NpmRegistry(upstream, options, new S360AdmissionClient(policy, options.S360));
        var metadata = await registry.MetadataAsync("@example/library");
        Assert.AreEqual(1, metadata["versions"]!.AsObject().Count);
        Assert.AreEqual(0, downloaded);
        var path = await registry.ArchiveAsync("@example/library", "1.0.0");
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        await registry.ArchiveAsync("@example/library", "1.0.0");
        Assert.AreEqual(1, downloaded);
        handler.Allowed = false;
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        handler.Unavailable = true;
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
    }

    [TestMethod]
    public async Task InternalPolicyRechecksHistoricalCacheWithoutRewritingItsProvenance()
    {
        var bytes = Archive();
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(bytes) : Response(Metadata(bytes))));
        using var oldPolicy = new HttpClient(new AdmissionHandler(bytes));
        var path = await Controlled(upstream, oldPolicy).ArchiveAsync("@example/library", "1.0.0");
        var sidecar = await File.ReadAllBytesAsync(path + ".json");
        var handler = new SinglePolicyHandler() { Allowed = false };
        using var policy = new HttpClient(handler);
        using var disconnected = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Historical cache must not fetch the old upstream.")));
        var options = Options(offline: true);
        options.Upstream = "https://registry.npmjs.org/";
        options.S360 = new S360AdmissionOptions { Enabled = true, Server = "https://s360.example/", CheckKey = "fixture", PolicySource = "https://nopkg.fixture/npm" };
        var registry = new NpmRegistry(disconnected, options, new S360AdmissionClient(policy, options.S360));
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        handler.Allowed = true;
        Assert.AreEqual(path, await registry.ArchiveAsync("@example/library", "1.0.0"));
        CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(path + ".json"));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        handler.Allowed = false;
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        handler.Unavailable = true;
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        handler.Unavailable = false;
        handler.Allowed = true;
        var tampered = JsonNode.Parse(sidecar)!;
        tampered["_nopkg"]!["Sha256"] = new string('a', 64);
        await File.WriteAllTextAsync(path + ".json", tampered.ToJsonString());
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
    }

    private NpmRegistry Controlled(HttpClient upstream, HttpClient policy, bool offline = false, string? source = null)
    {
        var options = Options(offline);
        if (source != null) options.Upstream = source;
        options.S360 = new S360AdmissionOptions { Enabled = true, Server = "https://s360.example/", CheckKey = "isolated-fixture-key" };
        return new NpmRegistry(upstream, options, new S360AdmissionClient(policy, options.S360));
    }

    [TestMethod]
    public async Task MetadataListsOnlyAdmittedVersionsWithoutDownloadingTheirArchives()
    {
        var bytes = Archive();
        var metadata = Metadata(bytes);
        metadata["versions"]!["2.0.0"] = metadata["versions"]!["1.0.0"]!.DeepClone();
        metadata["dist-tags"]!["latest"] = "2.0.0";
        metadata["dist-tags"]!["stable"] = "1.0.0";
        metadata["time"] = new JsonObject { ["created"] = "2026-01-01", ["1.0.0"] = "2026-01-01", ["2.0.0"] = "2026-02-01" };
        using var upstream = new HttpClient(new RegistryHandler(r =>
        {
            Assert.IsFalse(r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal));
            Assert.IsFalse(r.Headers.Contains("X-S360-Key"));
            return Response(metadata);
        }));
        using var policy = new HttpClient(new AdmissionHandler(bytes));
        var visible = await Controlled(upstream, policy).MetadataAsync("@example/library");
        Assert.AreEqual(1, visible["versions"]!.AsObject().Count);
        Assert.IsNull(visible["dist-tags"]!["latest"]);
        Assert.AreEqual("1.0.0", visible["dist-tags"]!["stable"]!.GetValue<string>());
        Assert.IsNull(visible["time"]!["2.0.0"]);
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tgz", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task RevocationBlocksCachedArchivesAndRemovesThemFromTheCatalog()
    {
        var bytes = Archive();
        var handler = new AdmissionHandler(bytes);
        using var policy = new HttpClient(handler);
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(bytes) : Response(Metadata(bytes))));
        var registry = Controlled(upstream, policy);
        var path = await registry.ArchiveAsync("@example/library", "1.0.0");
        Assert.IsNotNull(JsonNode.Parse(await File.ReadAllTextAsync(path + ".json"))!["_nopkg"]);
        Assert.AreEqual(1, (await registry.CachedPackagesAsync()).Count);
        handler.Allowed = false;
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        Assert.AreEqual(0, (await registry.CachedPackagesAsync()).Count);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => registry.MetadataAsync("@example/library"));
        Assert.IsTrue(File.Exists(path), "Revocation blocks delivery without deleting stored bytes.");
    }

    [TestMethod]
    public async Task PolicyOutageCannotFallBackToPreviouslyApprovedMetadataOrArchives()
    {
        var bytes = Archive();
        var handler = new AdmissionHandler(bytes);
        using var policy = new HttpClient(handler);
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(bytes) : Response(Metadata(bytes))));
        await Controlled(upstream, policy).ArchiveAsync("@example/library", "1.0.0");
        handler.Unavailable = true;
        using var disconnected = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Cache-only must not call the upstream.")));
        var offline = Controlled(disconnected, policy, offline: true);
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => offline.MetadataAsync("@example/library"));
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => offline.ArchiveAsync("@example/library", "1.0.0"));
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => offline.CachedPackagesAsync());
    }

    [TestMethod]
    public async Task RegistryRejectsCorruptArchiveIndependentlyOfS360Approval()
    {
        var bytes = Archive();
        using var policy = new HttpClient(new AdmissionHandler(bytes));
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response("corrupt archive"u8.ToArray()) : Response(Metadata(bytes))));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Controlled(upstream, policy).ArchiveAsync("@example/library", "1.0.0"));
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tgz", SearchOption.AllDirectories).Count());
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task RevocationDuringDownloadPreventsCommit()
    {
        var bytes = Archive();
        var handler = new AdmissionHandler(bytes);
        using var policy = new HttpClient(handler);
        using var upstream = new HttpClient(new RegistryHandler(r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)) return Response(Metadata(bytes));
            handler.Allowed = false;
            return Response(bytes);
        }));
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => Controlled(upstream, policy).ArchiveAsync("@example/library", "1.0.0"));
        Assert.IsTrue(handler.Assessments >= 3);
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tgz", SearchOption.AllDirectories).Count());
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task CachedProvenanceCannotBeReclassifiedWhenUpstreamChanges()
    {
        var bytes = Archive();
        var handler = new AdmissionHandler(bytes);
        using var policy = new HttpClient(handler);
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(bytes) : Response(Metadata(bytes))));
        await Controlled(upstream, policy).ArchiveAsync("@example/library", "1.0.0");
        handler.Source = "https://another.example";
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => Controlled(upstream, policy, offline: true,
            source: "https://another.example/").ArchiveAsync("@example/library", "1.0.0"));
    }

    [TestMethod]
    public async Task PreviewCacheRequiresCurrentComplianceBeforeAdoption()
    {
        var bytes = Archive();
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(bytes) : Response(Metadata(bytes))));
        var path = await new NpmRegistry(upstream, Options()).ArchiveAsync("@example/library", "1.0.0");
        var handler = new AdmissionHandler(bytes) { Allowed = false };
        using var policy = new HttpClient(handler);
        var controlled = Controlled(upstream, policy, offline: true);
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => controlled.ArchiveAsync("@example/library", "1.0.0"));
        Assert.IsNull(JsonNode.Parse(await File.ReadAllTextAsync(path + ".json"))!["_nopkg"]);
        handler.Allowed = true;
        Assert.AreEqual(path, await controlled.ArchiveAsync("@example/library", "1.0.0"));
        Assert.IsNotNull(JsonNode.Parse(await File.ReadAllTextAsync(path + ".json"))!["_nopkg"]);
    }

    [TestMethod]
    public async Task MismatchedPolicyDecisionCannotExposeMetadata()
    {
        var bytes = Archive();
        using var policy = new HttpClient(new AdmissionHandler(bytes) { MismatchedDecision = true });
        using var upstream = new HttpClient(new RegistryHandler(_ => Response(Metadata(bytes))));
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => Controlled(upstream, policy).MetadataAsync("@example/library"));
    }

    [TestMethod]
    public async Task UnavailableVulnerabilityDataCannotBeTreatedAsClean()
    {
        var bytes = Archive();
        using var policy = new HttpClient(new AdmissionHandler(bytes) { Pending = true });
        using var upstream = new HttpClient(new RegistryHandler(_ => Response(Metadata(bytes))));
        var registry = Controlled(upstream, policy);
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.MetadataAsync("@example/library"));
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
    }
}
