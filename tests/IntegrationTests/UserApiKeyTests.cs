using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Services.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.Tests.IntegrationTests;

[TestClass]
public class UserApiKeyTests : TestBase
{
    [TestMethod]
    public async Task EditingPreservesCredentialsAndStatisticsAndEnforcesOwnershipAndCsrf()
    {
        await LoginAsAdmin();
        var created = await PostForm("/UserApiKeys/Create", new Dictionary<string, string>
        {
            ["Name"] = "Release pipeline", ["ExpirationDays"] = "30"
        });
        var location = created.Headers.Location!.ToString();
        var html = await Http.GetStringAsync(location);
        var raw = Regex.Match(html, "id=\"new-api-key\"[^>]*>([^<]+)</code>").Groups[1].Value;
        Assert.AreEqual(43, raw.Length);
        int id;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
            id = (await db.UserApiKeys.SingleAsync(k => k.Name == "Release pipeline")).Id;
            await db.RecordUseAsync(id, default);
            await db.RecordPublicationAsync(id, "example.package", "1.2.3", default);
        }
        var edit = $"/UserApiKeys/Edit/{id}";
        var form = new Dictionary<string, string>
        {
            ["Name"] = " Production release ", ["ExpiresAt"] = "2035-06-01T12:30:45",
            ["KeyHash"] = "forged", ["TotalUses"] = "9000", ["PublishedPackagesCount"] = "9000"
        };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PostForm(edit, new(form), includeToken: false)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Found, (await PostForm(edit, new(form))).StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
            var key = await db.UserApiKeys.AsNoTracking().SingleAsync(k => k.Id == id);
            Assert.AreEqual("Production release", key.Name);
            Assert.AreEqual(new DateTime(2035, 6, 1, 12, 30, 45), key.ExpiresAt);
            Assert.AreEqual(PackagePublisherAuthenticationService.ComputeSha256Hex(raw), key.KeyHash);
            Assert.AreEqual(1L, key.TotalUses);
            Assert.AreEqual(1L, key.PublishedPackagesCount);
            Assert.AreEqual("example.package", key.LastPublishedPackage);
            var auth = scope.ServiceProvider.GetRequiredService<PackagePublisherAuthenticationService>();
            Assert.IsNotNull(await auth.AuthenticateAsync(raw, default));
        }
        var index = await Http.GetStringAsync("/UserApiKeys/Index");
        Assert.Contains("Production release", index);
        Assert.Contains("example.package", index);
        Assert.Contains("1.2.3", index);
        Assert.DoesNotContain(raw, index);
        Assert.DoesNotContain(PackagePublisherAuthenticationService.ComputeSha256Hex(raw), index);
        Assert.Contains("API key updated.", index);
        Assert.AreEqual(HttpStatusCode.OK, (await PostForm(edit, new Dictionary<string, string>
        {
            ["Name"] = "   ", ["ExpiresAt"] = "invalid"
        })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Found, (await PostForm(edit, new Dictionary<string, string>
        {
            ["Name"] = "Production release", ["ExpiresAt"] = ""
        })).StatusCode);
        using (var scope = Server!.Services.CreateScope())
            Assert.IsNull((await scope.ServiceProvider.GetRequiredService<NopkgDbContext>()
                .UserApiKeys.SingleAsync(k => k.Id == id)).ExpiresAt);
        await RegisterAndLoginAsync();
        Assert.DoesNotContain("Production release", await Http.GetStringAsync("/UserApiKeys/Index"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(edit)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm(edit, new(form), tokenUrl: "/UserApiKeys/Create")).StatusCode);
    }

    [TestMethod]
    public async Task EditingExpirationImmediatelyChangesAuthenticationWithoutRotatingTheKey()
    {
        await LoginAsAdmin();
        var created = await PostForm("/UserApiKeys/Create", new Dictionary<string, string>
        {
            ["Name"] = "Expiring key", ["ExpirationDays"] = "0"
        });
        var html = await Http.GetStringAsync(created.Headers.Location!.ToString());
        var raw = Regex.Match(html, "id=\"new-api-key\"[^>]*>([^<]+)</code>").Groups[1].Value;
        int id;
        using (var scope = Server!.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<NopkgDbContext>()
                .UserApiKeys.SingleAsync(k => k.Name == "Expiring key")).Id;
        var edit = $"/UserApiKeys/Edit/{id}";
        await PostForm(edit, new Dictionary<string, string> { ["Name"] = "Expired key", ["ExpiresAt"] = "2020-01-01T00:00:00" });
        using (var scope = Server!.Services.CreateScope())
        {
            Assert.IsNull(await scope.ServiceProvider.GetRequiredService<PackagePublisherAuthenticationService>().AuthenticateAsync(raw, default));
            Assert.AreEqual(0L, (await scope.ServiceProvider.GetRequiredService<NopkgDbContext>().UserApiKeys.SingleAsync(k => k.Id == id)).TotalUses);
        }
        await PostForm(edit, new Dictionary<string, string> { ["Name"] = "Renewed key", ["ExpiresAt"] = "" });
        using (var scope = Server!.Services.CreateScope())
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<PackagePublisherAuthenticationService>().AuthenticateAsync(raw, default));
        await PostForm($"/UserApiKeys/Delete/{id}", []);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(edit)).StatusCode);
    }
}
