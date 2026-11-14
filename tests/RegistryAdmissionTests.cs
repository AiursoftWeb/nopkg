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
                var nameFilter = request.RequestUri.Query.TrimStart('?').Split('&')
                    .FirstOrDefault(value => value.StartsWith("name=", StringComparison.Ordinal));
                var name = nameFilter == null ? null : Uri.UnescapeDataString(nameFilter[5..]);
                var selected = Allowed ? artifacts.Where(a => name == null || a.Name == name).Cast<ArtifactIdentity?>().ToList() : [];
                return Response(JsonSerializer.SerializeToNode(new ApprovedArtifactPage(selected, null, 1))!.AsObject());
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var identities = body["dependencies"]!.Deserialize<List<ArtifactIdentity>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assessments++;
            return Response(JsonSerializer.SerializeToNode(new ArtifactAssessmentResponse(identities.Select(a =>
                (ArtifactDecision?)new ArtifactDecision(MismatchedDecision ? a with { Name = "different-library" } : a,
                    Allowed && artifacts.Contains(a), Pending)).ToList()))!.AsObject());
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
        Assert.AreEqual(1, handler.Lookups, "A catalog must not fetch the policy separately for each package.");
        Assert.AreEqual(1, handler.Assessments, "Assess the current artifact decisions together.");
        var selected = await registry.CachedPackagesAsync(query: "CATALOG-ONE");
        Assert.AreEqual("catalog-one", selected.Single().Name);
        handler.CatalogArtifacts.Add(handler.CatalogArtifacts[0] with { Sha256 = new string('c', 64) });
        Assert.AreEqual("catalog-two", (await registry.CachedPackagesAsync()).Single().Name,
            "Conflicting hashes hide only that package/version, not another package with the same version.");
        handler.Pending = true;
        Assert.AreEqual(0, (await registry.CachedPackagesAsync()).Count);
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

    [TestMethod]
    public async Task ApprovedCatalogKeepsOnePaginationCeilingAndRejectsMalformedSource()
    {
        var requests = new List<Uri>();
        var sources = new Queue<string>(["https://registry.example", "https://registry.example", "https://different.example"]);
        using var policy = new HttpClient(new RegistryHandler(request =>
        {
            var next = request.RequestUri!.Query.Contains("afterId=1", StringComparison.Ordinal);
            if (next) Assert.Contains("upperId=2", request.RequestUri.Query);
            requests.Add(request.RequestUri);
            var artifact = new ArtifactIdentity("npm", next ? "catalog-two" : "catalog-one", "1.0.0",
                sources.Dequeue(), new string('a', 64));
            return Response(JsonSerializer.SerializeToNode(new ApprovedArtifactPage([artifact], next ? null : 1, 2))!.AsObject());
        }));
        var client = new S360AdmissionClient(policy, new S360AdmissionOptions
        {
            Enabled = true, Server = "https://s360.example", CheckKey = "isolated-fixture-key"
        });
        Assert.AreEqual(2, (await client.ApprovedCatalogAsync("https://registry.example", CancellationToken.None)).Count);
        Assert.AreEqual(2, requests.Count);
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => client.ApprovedCatalogAsync("https://registry.example", CancellationToken.None));
    }

    private sealed class SinglePolicyHandler(byte[] archive) : HttpMessageHandler
    {
        public bool Allowed { get; set; } = true;
        public bool Verified { get; set; }
        public bool DenyVerification { get; set; }
        public bool Unavailable { get; set; }
        public int Verifications { get; private set; }
        public int CandidateChecks { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Unavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var source = "https://nopkg.fixture/npm";
            object result;
            if (request.RequestUri!.AbsolutePath.EndsWith("/candidates", StringComparison.Ordinal))
            {
                CandidateChecks++;
                var candidates = json["candidates"]!.Deserialize<List<MirrorCandidate>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.IsTrue(candidates.All(c => c.Source == source));
                result = new MirrorCandidateResponse(candidates.Select(c => new MirrorCandidateDecision(c, Allowed && c.Version == "1.0.0")).ToList());
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/verify", StringComparison.Ordinal))
            {
                var artifact = json.Deserialize<ArtifactIdentity>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.AreEqual(source, artifact.Source);
                Verifications++;
                Verified = Allowed && !DenyVerification && artifact.Sha256 == Convert.ToHexStringLower(SHA256.HashData(archive));
                result = new ArtifactDecision(artifact, Verified, false);
            }
            else
            {
                var identities = json["dependencies"]!.Deserialize<List<ArtifactIdentity>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.IsTrue(identities.All(a => a.Source == source));
                result = new ArtifactAssessmentResponse(identities.Select(a => (ArtifactDecision?)new ArtifactDecision(a, Allowed && Verified, Allowed && !Verified)).ToList());
            }
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
        var handler = new SinglePolicyHandler(archive);
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
    public async Task SingleInternalPolicySupportsColdMirroringAndStillBlocksUnverifiedBytesAndRevokedCache()
    {
        var bytes = Archive();
        var downloaded = 0;
        using var upstream = new HttpClient(new RegistryHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)) { downloaded++; return Response(bytes); }
            return Response(Metadata(bytes));
        }));
        var handler = new SinglePolicyHandler(bytes);
        using var policy = new HttpClient(handler);
        var options = Options();
        options.S360 = new S360AdmissionOptions { Enabled = true, Server = "https://s360.example/", CheckKey = "fixture", PolicySource = "https://nopkg.fixture/npm" };
        var registry = new NpmRegistry(upstream, options, new S360AdmissionClient(policy, options.S360));
        var metadata = await registry.MetadataAsync("@example/library");
        Assert.AreEqual(1, metadata["versions"]!.AsObject().Count);
        Assert.AreEqual(0, downloaded);
        handler.DenyVerification = true;
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tgz", SearchOption.AllDirectories).Count());
        handler.DenyVerification = false;
        var path = await registry.ArchiveAsync("@example/library", "1.0.0");
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(2, handler.Verifications);
        await registry.ArchiveAsync("@example/library", "1.0.0");
        Assert.AreEqual(2, downloaded);
        handler.Allowed = false;
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        handler.Unavailable = true;
        await Assert.ThrowsExactlyAsync<AdmissionUnavailableException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
    }

    [TestMethod]
    public async Task InternalPolicyReverifiesHistoricalCacheWithoutRewritingItsProvenance()
    {
        var bytes = Archive();
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(bytes) : Response(Metadata(bytes))));
        using var oldPolicy = new HttpClient(new AdmissionHandler(bytes));
        var path = await Controlled(upstream, oldPolicy).ArchiveAsync("@example/library", "1.0.0");
        var sidecar = await File.ReadAllBytesAsync(path + ".json");
        var handler = new SinglePolicyHandler(bytes) { DenyVerification = true };
        using var policy = new HttpClient(handler);
        using var disconnected = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Historical cache must not fetch the old upstream.")));
        var options = Options(offline: true);
        options.Upstream = "https://registry.npmjs.org/";
        options.S360 = new S360AdmissionOptions { Enabled = true, Server = "https://s360.example/", CheckKey = "fixture", PolicySource = "https://nopkg.fixture/npm" };
        var registry = new NpmRegistry(disconnected, options, new S360AdmissionClient(policy, options.S360));
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
        Assert.AreEqual(1, handler.Verifications);
        handler.DenyVerification = false;
        Assert.AreEqual(path, await registry.ArchiveAsync("@example/library", "1.0.0"));
        Assert.AreEqual(2, handler.Verifications);
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
    public async Task CorrectNpmIntegrityCannotReplaceS360RawArtifactHash()
    {
        var bytes = Archive();
        using var policy = new HttpClient(new AdmissionHandler(bytes) { Hash = new string('a', 64) });
        using var upstream = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(bytes) : Response(Metadata(bytes))));
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => Controlled(upstream, policy).ArchiveAsync("@example/library", "1.0.0"));
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
    public async Task PreviewCacheRequiresExactIndependentAdmissionBeforeAdoption()
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
    public async Task PendingVerificationIsNotAnApprovedVersion()
    {
        var bytes = Archive();
        using var policy = new HttpClient(new AdmissionHandler(bytes) { Pending = true });
        using var upstream = new HttpClient(new RegistryHandler(_ => Response(Metadata(bytes))));
        var registry = Controlled(upstream, policy);
        Assert.AreEqual(0, (await registry.MetadataAsync("@example/library"))["versions"]!.AsObject().Count);
        await Assert.ThrowsExactlyAsync<ArtifactDeniedException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
    }
}
