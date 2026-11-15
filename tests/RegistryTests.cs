using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Registry;

namespace Aiursoft.Nopkg.Tests;

[TestClass]
public partial class RegistryTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nopkg-tests-" + Guid.NewGuid());

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private RegistryOptions Options(bool offline = false) => new()
    {
        StoragePath = _directory,
        Upstream = "https://registry.example/",
        CacheOnly = offline
    };

    private static byte[] Archive(string name = "@example/library", string directory = "package", string version = "1.0.0")
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes(new JsonObject
               { ["name"] = name, ["version"] = version }.ToJsonString())))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, directory + "/package.json") { DataStream = data });
        return output.ToArray();
    }

    private static JsonObject Metadata(byte[] archive) => new()
    {
        ["name"] = "@example/library",
        ["dist-tags"] = new JsonObject { ["latest"] = "1.0.0" },
        ["versions"] = new JsonObject
        {
            ["1.0.0"] = new JsonObject
            {
                ["name"] = "@example/library", ["version"] = "1.0.0",
                ["dist"] = new JsonObject
                {
                    ["tarball"] = "https://registry.example/library.tgz",
                    ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(archive))
                }
            }
        }
    };

    private sealed class RegistryHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Response(JsonObject json) => Response(Encoding.UTF8.GetBytes(json.ToJsonString()));

    [TestMethod]
    public async Task ReadmeUsesCachedArchiveWithoutExtractingOrContactingUpstream()
    {
        var directory = Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("@example/library")))) ;
        Directory.CreateDirectory(directory);
        await using (var file = File.Create(Path.Combine(directory, "1.0.0.tgz")))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip))
        {
            using var unsafeData = new MemoryStream(Encoding.UTF8.GetBytes("not the readme"));
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "../README.md") { DataStream = unsafeData });
            using var data = new MemoryStream(Encoding.UTF8.GetBytes("# Package overview"));
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "package/README.md") { DataStream = data });
        }
        using var upstream = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("No upstream requests.")));
        var registry = new NpmRegistry(upstream, Options());
        Assert.AreEqual("# Package overview", await registry.CachedReadmeAsync("@example/library", "1.0.0", CancellationToken.None));
        Assert.AreEqual("", await registry.CachedReadmeAsync("@example/library", "2.0.0", CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "README.md")));
    }

    [TestMethod]
    public async Task VerifiedPackageSurvivesOfflineRestartAndDetectsCacheCorruption()
    {
        var archive = Archive();
        using var client = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(archive) : Response(Metadata(archive))));
        var registry = new NpmRegistry(client, Options());
        var path = await registry.ArchiveAsync("@example/library", "1.0.0");
        CollectionAssert.AreEqual(archive, await File.ReadAllBytesAsync(path));
        using var unavailable = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Offline mode must not request an upstream.")));
        var restarted = new NpmRegistry(unavailable, Options(offline: true));
        Assert.AreEqual(path, await restarted.ArchiveAsync("@example/library", "1.0.0"));
        Assert.AreEqual("@example/library", (await restarted.MetadataAsync("@example/library"))["name"]!.GetValue<string>());
        await File.WriteAllTextAsync(path, "corrupted");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.ArchiveAsync("@example/library", "1.0.0"));
    }

    [TestMethod]
    public async Task ChangedUpstreamVersionCannotReplaceVerifiedCachedBytes()
    {
        var archive = Archive();
        var metadata = Metadata(archive);
        using var client = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(archive) : Response(metadata)));
        var registry = new NpmRegistry(client, Options());
        var path = await registry.ArchiveAsync("@example/library", "1.0.0");
        metadata["versions"]!["1.0.0"]!["dist"]!["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData("changed"u8));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => registry.MetadataAsync("@example/library"));
        CollectionAssert.AreEqual(archive, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task ForeignTarballOriginIsRejectedBeforeDownload()
    {
        var metadata = Metadata(Archive());
        metadata["versions"]!["1.0.0"]!["dist"]!["tarball"] = "https://different.example/library.tgz";
        using var client = new HttpClient(new RegistryHandler(r =>
        {
            Assert.AreEqual("registry.example", r.RequestUri!.Host);
            Assert.IsFalse(r.RequestUri.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal));
            return Response(metadata);
        }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new NpmRegistry(client, Options()).ArchiveAsync("@example/library", "1.0.0"));
    }

    [TestMethod]
    public async Task CorrectHashDoesNotAllowWrongPackageIdentity()
    {
        var archive = Archive("different-library");
        using var client = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(archive) : Response(Metadata(archive))));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new NpmRegistry(client, Options()).ArchiveAsync("@example/library", "1.0.0"));
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tgz", SearchOption.AllDirectories).Count());
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task InvalidUpstreamVersionCannotBecomeACachePath()
    {
        var metadata = Metadata(Archive());
        metadata["versions"]!["../../outside"] = metadata["versions"]!["1.0.0"]!.DeepClone();
        using var upstream = new HttpClient(new RegistryHandler(_ => Response(metadata)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new NpmRegistry(upstream, Options()).MetadataAsync("@example/library"));
        Assert.IsFalse(Directory.Exists(_directory), "Invalid version metadata must not be persisted.");
    }


    [TestMethod]
    [DataRow("json-schema", true)]
    [DataRow(".", false)]
    [DataRow("..", false)]
    public async Task NpmArchiveDirectoryMayVaryButMustBeSafe(string directory, bool allowed)
    {
        var archive = Archive(directory: directory);
        using var client = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(archive) : Response(Metadata(archive))));
        var registry = new NpmRegistry(client, Options());
        if (allowed)
            CollectionAssert.AreEqual(archive, await File.ReadAllBytesAsync(await registry.ArchiveAsync("@example/library", "1.0.0")));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => registry.ArchiveAsync("@example/library", "1.0.0"));
    }

    [TestMethod]
    public async Task NpmArchiveAllowsEquivalentLegacyVersionPrefix()
    {
        var archive = Archive(version: "v1.0.0");
        using var client = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(archive) : Response(Metadata(archive))));
        CollectionAssert.AreEqual(archive, await File.ReadAllBytesAsync(await new NpmRegistry(client, Options()).ArchiveAsync("@example/library", "1.0.0")));
    }

    [TestMethod]
    public async Task LargeUpstreamCatalogCanSupplyAnExactVersion()
    {
        var archive = Archive();
        var metadata = Metadata(archive);
        // Real registries can exceed 32 MiB after years of version history.
        metadata["readme"] = new string('x', 33 * 1024 * 1024);
        using var client = new HttpClient(new RegistryHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? Response(archive) : Response(metadata)));
        var registry = new NpmRegistry(client, Options());
        CollectionAssert.AreEqual(archive, await File.ReadAllBytesAsync(await registry.ArchiveAsync("@example/library", "1.0.0")));
    }

    [TestMethod]
    public async Task OversizedCatalogIsRejectedWithoutReplacingCachedMetadata()
    {
        var metadata = Metadata(Archive());
        using var client = new HttpClient(new RegistryHandler(_ => Response(metadata)));
        var options = Options();
        options.MaxMetadataBytes = 1024;
        var registry = new NpmRegistry(client, options);
        await registry.MetadataAsync("@example/library");

        metadata["readme"] = new string('x', 1024);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => registry.MetadataAsync("@example/library"));

        using var unavailable = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Offline reads must not request an upstream.")));
        var cached = await new NpmRegistry(unavailable, Options(offline: true)).MetadataAsync("@example/library");
        Assert.IsFalse(cached.ContainsKey("readme"));
        Assert.IsTrue(cached["versions"]!.AsObject().ContainsKey("1.0.0"));
    }

}
