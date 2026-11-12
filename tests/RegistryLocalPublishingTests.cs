using System.Security.Cryptography;
using Aiursoft.Nopkg.Registry;
namespace Aiursoft.Nopkg.Tests;

public partial class RegistryTests
{
    private sealed class OwnedStore(LocalPackage local) : ILocalPackageStore
    {
        public Task<LocalPackage?> FindAsync(string name, CancellationToken token) => Task.FromResult<LocalPackage?>(local);
        public Task<bool> RecordMirrorAsync(string name,CancellationToken token) => throw new AssertFailedException("An internal package must not be registered as a mirror.");
        public Task<IReadOnlyList<CachedPackage>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<CachedPackage>>([new("@example/library",1,1,true)]);
    }
    [TestMethod]
    public async Task InternalPackageDoesNotContactUpstreamOrS360AndNeverFillsMissingVersions()
    {
        var archive=Archive();var hash=Convert.ToHexStringLower(SHA256.HashData(archive));
        var options=Options();options.S360=new S360AdmissionOptions {Enabled=true,Server="https://policy.example/",CheckKey="test-only"};
        using var unavailable=new HttpClient(new RegistryHandler(_=>throw new AssertFailedException("Internal content must not request an upstream or S360.")));
        var registry=new NpmRegistry(unavailable,options,new S360AdmissionClient(unavailable,options.S360),new OwnedStore(new LocalPackage(Metadata(archive),new Dictionary<string,string>{{"1.0.0",hash}})));
        var path=registry.PublishedArchivePath(hash);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,archive);
        Assert.AreEqual("@example/library",(await registry.MetadataAsync("@example/library"))["name"]!.GetValue<string>());
        Assert.AreEqual(path,await registry.ArchiveAsync("@example/library","1.0.0"));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(()=>registry.ArchiveAsync("@example/library","2.0.0"));
        await File.WriteAllTextAsync(path,"tampered");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>registry.ArchiveAsync("@example/library","1.0.0"));
    }
}
