namespace Aiursoft.Nopkg.Tests.IntegrationTests;

[TestClass]
public class HomeControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex()
    {
        var url = "/";
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("registry-hero", page);
        Assert.Contains("quick-start", page);
        Assert.Contains("name=\"q\"", page);
        Assert.DoesNotContain("template-docs.aiursoft.com", page);
    }
    [TestMethod]
    public async Task HomeRespectsTheSavedColorTheme()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", "prefer-dark=True");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Contains("data-bs-theme=\"dark\"", await response.Content.ReadAsStringAsync());
    }
}
