using System.IO.Compression;
using System.Net;
using System.Text;
using Aiursoft.Nopkg.Registry;

namespace Aiursoft.Nopkg.Tests.IntegrationTests;

[TestClass]
public class AuditTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        await base.CreateServer();
        Server!.Services.GetRequiredService<RegistryOptions>().CacheOnly = true;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValidBulkAuditReachesTheRegistryAndDoesNotClaimOfflineSafety(bool compressed)
    {
        using var data = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes("{\"lodash\":[\"4.17.20\"]}");
        if (compressed)
        {
            using (var gzip = new GZipStream(data, CompressionMode.Compress, leaveOpen: true))
                await gzip.WriteAsync(bytes);
            bytes = data.ToArray();
        }
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new("application/json");
        if (compressed) body.Headers.ContentEncoding.Add("gzip");
        using var response = await Http.PostAsync("/npm/-/npm/v1/security/advisories/bulk", body);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "cache-only");
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("[]")]
    [DataRow("{\"lodash\":false}")]
    public async Task InvalidBulkAuditIsAClientError(string document)
    {
        using var response = await Http.PostAsync("/npm/-/npm/v1/security/advisories/bulk", new StringContent(document, Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("gzip", HttpStatusCode.BadRequest)]
    [DataRow("br", HttpStatusCode.UnsupportedMediaType)]
    public async Task InvalidAuditCompressionIsAClientError(string encoding, HttpStatusCode expected)
    {
        using var body = new ByteArrayContent(Encoding.UTF8.GetBytes("invalid-compression"));
        body.Headers.ContentEncoding.Add(encoding);
        using var response = await Http.PostAsync("/npm/-/npm/v1/security/advisories/bulk", body);
        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task GzipExpansionCannotBypassTheAuditBodyLimit()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(new string(' ', 4 * 1024 * 1024 + 1)));
        using var body = new ByteArrayContent(bytes.ToArray());
        body.Headers.ContentEncoding.Add("gzip");
        using var response = await Http.PostAsync("/npm/-/npm/v1/security/advisories/bulk", body);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [TestMethod]
    public async Task UnsupportedSecurityRoutesAndMethodsDoNotReturnServerErrors()
    {
        using var get = await Http.GetAsync("/npm/-/npm/v1/security/advisories/bulk");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var quick = await Http.PostAsync("/npm/-/npm/v1/security/audits/quick", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.NotFound, quick.StatusCode);
    }
}
