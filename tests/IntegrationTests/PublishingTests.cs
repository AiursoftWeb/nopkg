using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Authorization;
using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Registry;
using Aiursoft.Nopkg.Services.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace Aiursoft.Nopkg.Tests.IntegrationTests;

[TestClass]
public class PublishingTests : TestBase
{
    [TestMethod]
    public async Task CatalogSearchAndDetailsKeepTheSelectedPackageAndRegistry()
    {
        var (_, key) = await PublisherAsync();
        Assert.AreEqual(HttpStatusCode.Created, (await PublishAsync("@test/design", key, Publication("@test/design"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await PublishAsync("@test/other", key, Publication("@test/other"))).StatusCode);
        var catalog = await Http.GetStringAsync("/Dashboard/Index?q=DESIGN");
        Assert.Contains("@test/design", catalog);
        Assert.DoesNotContain("@test/other", catalog);
        var details = await Http.GetStringAsync("/Package/Details?id=%40test%2Fdesign&version=1.0.0&q=design");
        Assert.Contains("npm install @test/design@1.0.0 --registry", details);
        Assert.Contains($"{Http.BaseAddress}npm/", details);
        Assert.Contains("q=design", details);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/Package/Details?id=%40test%2Fdesign&version=9.0.0")).StatusCode);
        var empty = await Http.GetStringAsync("/Dashboard/Index?q=not-found");
        Assert.Contains("No packages found", empty);
        Assert.DoesNotContain("No packages cached yet", empty);
    }

    private string _registryPath = null!;
    [TestInitialize]
    public override async Task CreateServer()
    {
        await base.CreateServer();
        _registryPath = Directory.CreateTempSubdirectory("nopkg-publishing-").FullName;
        var options = Server!.Services.GetRequiredService<RegistryOptions>();
        options.StoragePath = _registryPath;options.CacheOnly = true;
    }
    [TestCleanup]
    public override async Task CleanServer()
    {
        await base.CleanServer();
        if (Directory.Exists(_registryPath)) Directory.Delete(_registryPath, true);
    }
    private async Task<(string user, string key)> PublisherAsync(bool permitted = true)
    {
        using var scope = Server!.Services.CreateScope();var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
        var user = new User { UserName = Guid.NewGuid()+"@example.test", DisplayName = "Publisher" };
        db.Users.Add(user);
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        db.UserApiKeys.Add(new UserApiKey { UserId=user.Id,Name="Test key",KeyHash=PackagePublisherAuthenticationService.ComputeSha256Hex(raw),KeyPrefix=raw[..8] });
        if (permitted) db.UserClaims.Add(new IdentityUserClaim<string> {UserId=user.Id,ClaimType=AppPermissions.Type,ClaimValue=AppPermissionNames.CanPublishPackages});
        await db.SaveChangesAsync();return (user.Id,raw);
    }
    private static JsonObject Publication(string name, string version = "1.0.0", string? archiveName = null)
    {
        var manifest=new JsonObject { ["name"]=archiveName??name,["version"]=version,["scripts"]=new JsonObject { ["test"]="node -e true" } };
        using var archive=new MemoryStream();
        using (var gzip=new GZipStream(archive,CompressionMode.Compress,true))
        using (var writer=new TarWriter(gzip,leaveOpen:true))
        {
            var entry=new PaxTarEntry(TarEntryType.RegularFile,"package/package.json") {DataStream=new MemoryStream(Encoding.UTF8.GetBytes(manifest.ToJsonString()))};
            writer.WriteEntry(entry);
        }
        var bytes=archive.ToArray();
        return new JsonObject { ["name"]=name,["versions"]=new JsonObject { [version]=new JsonObject { ["name"]=name,["version"]=version } },
            ["dist-tags"]=new JsonObject { ["latest"]=version },["_attachments"]=new JsonObject { ["package.tgz"]=new JsonObject { ["data"]=Convert.ToBase64String(bytes),["length"]=bytes.Length } } };
    }
    private async Task<HttpResponseMessage> PublishAsync(string name,string key,JsonObject? document = null)
    {
        var request=new HttpRequestMessage(HttpMethod.Put,"/npm/"+Uri.EscapeDataString(name));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Content=new StringContent((document??Publication(name)).ToJsonString(),Encoding.UTF8,"application/json");
        return await Http.SendAsync(request);
    }
    [TestMethod]
    public async Task PublishScopedPackageUsesRealManifestAndServesImmutableBytes()
    {
        var (_,key)=await PublisherAsync();var doc=Publication("@test/owned");
        doc["versions"]!["1.0.0"]!["scripts"]=new JsonObject { ["install"]="untrusted-metadata-only" };
        using var response=await PublishAsync("@test/owned",key,doc);Assert.AreEqual(HttpStatusCode.Created,response.StatusCode);
        var metadata=JsonNode.Parse(await Http.GetStringAsync("/npm/@test%2fowned"))!;
        Assert.AreEqual("node -e true",metadata["versions"]!["1.0.0"]!["scripts"]!["test"]!.GetValue<string>());
        Assert.IsNull(metadata["versions"]!["1.0.0"]!["scripts"]!["install"]);
        var url=metadata["versions"]!["1.0.0"]!["dist"]!["tarball"]!.GetValue<string>();
        var bytes=await Http.GetByteArrayAsync(url);
        CollectionAssert.AreEqual(Convert.FromBase64String(doc["_attachments"]!["package.tgz"]!["data"]!.GetValue<string>()),bytes);
        using var duplicate=await PublishAsync("@test/owned",key,doc);Assert.AreEqual(HttpStatusCode.Conflict,duplicate.StatusCode);
    }
    [TestMethod]
    public async Task MissingPermissionAndOtherOwnerCannotPublish()
    {
        var (_,owner)=await PublisherAsync();var (_,other)=await PublisherAsync();var (_,denied)=await PublisherAsync(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized,(await PublishAsync("owned","invalid-key")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,(await PublishAsync("owned",denied)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created,(await PublishAsync("owned",owner)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,(await PublishAsync("owned",other,Publication("owned","1.0.1"))).StatusCode);
    }
    [TestMethod]
    public async Task RevocationExpiryAndPermissionChangesTakeEffectOnNextRequest()
    {
        var (user,key)=await PublisherAsync();Assert.AreEqual(HttpStatusCode.Created,(await PublishAsync("first",key)).StatusCode);
        using(var scope=Server!.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<NopkgDbContext>();db.UserClaims.RemoveRange(await db.UserClaims.Where(x=>x.UserId==user).ToListAsync());await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Forbidden,(await PublishAsync("second",key)).StatusCode);
        using(var scope=Server!.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<NopkgDbContext>();var record=await db.UserApiKeys.SingleAsync(x=>x.UserId==user);record.ExpiresAt=DateTime.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized,(await PublishAsync("second",key)).StatusCode);
        using(var scope=Server!.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<NopkgDbContext>();db.UserApiKeys.Remove(await db.UserApiKeys.SingleAsync(x=>x.UserId==user));await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized,(await PublishAsync("second",key)).StatusCode);
    }
    [TestMethod]
    public async Task MirroredPackageCannotBeClaimedByOrdinaryUpload()
    {
        var (_,key)=await PublisherAsync();
        using(var scope=Server!.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<NopkgDbContext>();db.PackageFamilies.Add(new PackageFamily {Name="third-party"});await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Forbidden,(await PublishAsync("third-party",key)).StatusCode);
    }
    [TestMethod]
    public async Task AdministratorPublicationConvertsMirrorAndNeverFallsBackAfterOwnerDeletion()
    {
        const string name = "converted-mirror";
        var (user, key) = await PublisherAsync();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
            db.PackageFamilies.Add(new PackageFamily { Name = name });
            db.UserClaims.Add(new IdentityUserClaim<string> { UserId = user, ClaimType = AppPermissions.Type,
                ClaimValue = AppPermissionNames.CanManagePackages });
            await db.SaveChangesAsync();
        }
        var publication = Publication(name, "2.0.0");
        Assert.AreEqual(HttpStatusCode.Created, (await PublishAsync(name, key, publication)).StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
            var family = await db.PackageFamilies.SingleAsync(f => f.Name == name);
            Assert.IsTrue(family.IsLocallyPublished);
            Assert.AreEqual(user, family.OwnerUserId);
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user));
            await db.SaveChangesAsync();
        }
        var metadata = JsonNode.Parse(await Http.GetStringAsync("/npm/" + name))!;
        Assert.AreEqual(1, metadata["versions"]!.AsObject().Count);
        Assert.AreEqual("2.0.0", metadata["dist-tags"]!["latest"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/npm/{name}/-/{name}-9.9.9.tgz")).StatusCode);
        CollectionAssert.AreEqual(Convert.FromBase64String(publication["_attachments"]!["package.tgz"]!["data"]!.GetValue<string>()),
            await Http.GetByteArrayAsync($"/npm/{name}/-/{name}-2.0.0.tgz"));
        using var verification = Server!.Services.CreateScope();
        var familyAfter = await verification.ServiceProvider.GetRequiredService<NopkgDbContext>().PackageFamilies.SingleAsync(f => f.Name == name);
        Assert.IsTrue(familyAfter.IsLocallyPublished);
        Assert.IsNull(familyAfter.OwnerUserId);
    }

    [TestMethod]
    public async Task WrongArchiveIdentityIsRejectedWithoutReservingName()
    {
        var (_,key)=await PublisherAsync();Assert.AreEqual(HttpStatusCode.BadRequest,(await PublishAsync("claimed",key,Publication("claimed",archiveName:"different"))).StatusCode);
        using var scope=Server!.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<NopkgDbContext>();Assert.IsFalse(await db.PackageFamilies.AnyAsync(x=>x.Name=="claimed"));
    }
    [TestMethod]
    public async Task DistributionTagsAreOwnedAndMustReferencePublishedVersions()
    {
        var (_,key)=await PublisherAsync();var (_,other)=await PublisherAsync();await PublishAsync("tagged",key);
        async Task<HttpResponseMessage> Tag(string token,string version)
        {
            var r=new HttpRequestMessage(HttpMethod.Put,"/npm/-/package/tagged/dist-tags/stable");r.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);r.Content=new StringContent("\""+version+"\"",Encoding.UTF8,"application/json");return await Http.SendAsync(r);
        }
        Assert.AreEqual(HttpStatusCode.Forbidden,(await Tag(other,"1.0.0")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound,(await Tag(key,"9.0.0")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,(await Tag(key,"1.0.0")).StatusCode);
        var tags=JsonNode.Parse(await Http.GetStringAsync("/npm/-/package/tagged/dist-tags"))!;Assert.AreEqual("1.0.0",tags["stable"]!.GetValue<string>());
    }
    [TestMethod]
    public async Task PersonalKeyIsShownOnceAndStoredAsHash()
    {
        await LoginAsAdmin();
        var response = await PostForm("/UserApiKeys/Create", new Dictionary<string,string> { ["Name"]="Web key",["ExpirationDays"]="1" });
        Assert.AreEqual(HttpStatusCode.Found,response.StatusCode);
        var location = response.Headers.Location!.ToString();
        var html = await Http.GetStringAsync(location);
        var match = System.Text.RegularExpressions.Regex.Match(html,"<code id=\"new-api-key\"[^>]*>([^<]+)</code>");
        Assert.IsTrue(match.Success);var raw=match.Groups[1].Value;
        Assert.DoesNotContain(raw,await Http.GetStringAsync(location));
        using var scope=Server!.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
        var saved=await db.UserApiKeys.SingleAsync(x=>x.Name=="Web key");
        Assert.AreEqual(PackagePublisherAuthenticationService.ComputeSha256Hex(raw),saved.KeyHash);
        Assert.AreNotEqual(raw,saved.KeyHash);
    }

    [TestMethod]
    public async Task WrongJsonTypesAndOversizedBodyReturnClientErrors()
    {
        var (_,key)=await PublisherAsync();var body=Publication("invalid-types");body["name"]=true;
        Assert.AreEqual(HttpStatusCode.BadRequest,(await PublishAsync("invalid-types",key,body)).StatusCode);
        var request=new HttpRequestMessage(HttpMethod.Put,"/npm/-/package/invalid-types/dist-tags/stable");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Content=new StringContent("{}",Encoding.UTF8,"application/json");
        Assert.AreEqual(HttpStatusCode.BadRequest,(await Http.SendAsync(request)).StatusCode);
        request=new HttpRequestMessage(HttpMethod.Put,"/npm/-/package/invalid-types/dist-tags/stable");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Content=new StringContent(new string('x',2048),Encoding.UTF8,"application/json");
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge,(await Http.SendAsync(request)).StatusCode);
    }

    [TestMethod]
    public async Task InternalPackagesRemainVisibleWhenMirrorAdmissionIsDown()
    {
        var options=Server!.Services.GetRequiredService<RegistryOptions>();
        options.S360=new S360AdmissionOptions {Enabled=true,Server="http://127.0.0.1:9/",CheckKey="test-only"};
        var (_,key)=await PublisherAsync();
        Assert.AreEqual(HttpStatusCode.Created,(await PublishAsync("still-available",key)).StatusCode);
        var name="unavailable-mirror";
        var directory=Path.Combine(_registryPath,Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"metadata.json"),new JsonObject { ["name"]=name,["versions"]=new JsonObject { ["1.0.0"]=new JsonObject { ["name"]=name,["version"]="1.0.0" } } }.ToJsonString());
        using var response=await Http.GetAsync("/Dashboard");
        Assert.AreEqual(HttpStatusCode.OK,response.StatusCode);
        var html=await response.Content.ReadAsStringAsync();
        Assert.Contains("still-available",html);
        Assert.Contains("Mirror admission is unavailable",html);
    }

    [TestMethod]
    [DataRow("standard-package", "")]
    [DataRow("@test/standard-package", "")]
    [DataRow("npm", "")]
    [DataRow("prefixed-standard-package", "/npm")]
    [DataRow("@test/prefixed-standard-package", "/npm")]
    public async Task StandardTarballPathsUseTheSameImmutableArchive(string name, string registryPrefix)
    {
        var (_, key) = await PublisherAsync();
        var publication = Publication(name);
        Assert.AreEqual(HttpStatusCode.Created, (await PublishAsync(name, key, publication)).StatusCode);
        var leaf = name.Split('/')[^1];
        var url = registryPrefix + "/" + name + "/-/" + leaf + "-1.0.0.tgz";
        var metadata = JsonNode.Parse(await Http.GetStringAsync("/npm/" + name))!;
        var archiveUrl = new Uri(metadata["versions"]!["1.0.0"]!["dist"]!["tarball"]!.GetValue<string>());
        Assert.AreEqual("/" + name + "/-/" + leaf + "-1.0.0.tgz", archiveUrl.AbsolutePath);
        var bytes = await Http.GetByteArrayAsync(url);
        CollectionAssert.AreEqual(Convert.FromBase64String(publication["_attachments"]!["package.tgz"]!["data"]!.GetValue<string>()), bytes);
        using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(bytes.Length, head.Content.Headers.ContentLength);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(registryPrefix + "/" + name + "/-/" + leaf + "-9.9.9.tgz")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/npm/" + name + "/-/wrong-1.0.0.tgz")).StatusCode);
    }

    [TestMethod]
    [DataRow("GET", "")]
    [DataRow("HEAD", "")]
    [DataRow("GET", "/npm")]
    [DataRow("HEAD", "/npm")]
    public async Task StandardTarballPathRequiresAdmissionEvenWithConditionalHeaders(string method, string registryPrefix)
    {
        var options = Server!.Services.GetRequiredService<RegistryOptions>();
        options.CacheOnly = false;
        options.S360 = new S360AdmissionOptions { Enabled = true, Server = "http://127.0.0.1:9/", CheckKey = "test-only" };
        using var request = new HttpRequestMessage(new HttpMethod(method), registryPrefix + "/@test/mirror/-/mirror-1.0.0.tgz");
        request.Headers.Range = new RangeHeaderValue(0, 10);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

}
