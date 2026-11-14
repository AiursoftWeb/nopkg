using System.Net;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Registry;

namespace Aiursoft.Nopkg.Tests;

public partial class RegistryTests
{
    [TestMethod]
    public async Task BulkAuditPreservesRealAdvisoriesAndUsesConfiguredUpstream()
    {
        var payload = new JsonObject { ["lodash"] = new JsonArray("4.17.20") };
        var advisories = new JsonObject { ["lodash"] = new JsonArray(new JsonObject
            { ["id"] = 123, ["severity"] = "high", ["vulnerable_versions"] = "<4.17.21" }) };
        using var client = new HttpClient(new RegistryHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://registry.example/-/npm/v1/security/advisories/bulk", request.RequestUri!.ToString());
            Assert.IsNull(request.Headers.Authorization);
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.AreEqual((long)System.Text.Encoding.UTF8.GetByteCount(payload.ToJsonString()), request.Content.Headers.ContentLength);
            var submitted = JsonNode.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.IsTrue(JsonNode.DeepEquals(payload, submitted));
            return Response(advisories);
        }));
        var result = await new NpmRegistry(client, Options()).AuditAsync(payload);
        Assert.IsTrue(JsonNode.DeepEquals(advisories, result));
    }

    [TestMethod]
    public async Task BulkAuditDoesNotInventAnEmptyResultWhenUpstreamFails()
    {
        using var client = new HttpClient(new RegistryHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new NpmRegistry(client, Options()).AuditAsync(new JsonObject()));
    }

    [TestMethod]
    public async Task CacheOnlyAuditFailsWithoutContactingTheUpstream()
    {
        using var client = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Cache-only must not connect.")));
        await Assert.ThrowsAsync<HttpRequestException>(() => new NpmRegistry(client, Options(true)).AuditAsync(new JsonObject()));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{\"error\":\"Unavailable\"}")]
    [DataRow("{\"lodash\":null}")]
    public async Task BulkAuditRejectsInvalidUpstreamDocuments(string body)
    {
        using var client = new HttpClient(new RegistryHandler(_ => Response(System.Text.Encoding.UTF8.GetBytes(body))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new NpmRegistry(client, Options()).AuditAsync(new JsonObject()));
    }

    [TestMethod]
    [DataRow("{\"../package\":[\"1.0.0\"]}")]
    [DataRow("{\"package\":\"1.0.0\"}")]
    [DataRow("{\"package\":[null]}")]
    [DataRow("{\"package\":[1]}")]
    public async Task BulkAuditRejectsInvalidPackagesBeforeContactingTheUpstream(string body)
    {
        using var client = new HttpClient(new RegistryHandler(_ => throw new AssertFailedException("Invalid input must not connect.")));
        await Assert.ThrowsAsync<ArgumentException>(() => new NpmRegistry(client, Options()).AuditAsync(JsonNode.Parse(body)!.AsObject()));
    }
}
