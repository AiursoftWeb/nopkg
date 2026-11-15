using System.Globalization;
using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Models.DashboardViewModels;
using Aiursoft.Nopkg.Registry;
using Aiursoft.Nopkg.Services;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.Nopkg.Controllers;

[LimitPerMin]
public class PackageController(NpmRegistry registry, ILocalPackageStore localPackages) : Controller
{
    [HttpGet("/packages/details", Name = "NpmPackageDetails")]
    [HttpGet("/Package/Details/{id?}")]
    public async Task<IActionResult> Details(string id, string? version = null, string q = "", int page = 1,
        string tab = "overview", int versionsPage = 1, int dependentsPage = 1, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            // Accept old bookmarked scoped paths too; all new links use the named query route.
            id = NpmRegistry.PackageName(Uri.UnescapeDataString(id));
            var metadata = await registry.CatalogMetadataAsync(id, cancellationToken);
            var versions = metadata["versions"]!.AsObject();
            var ordered = versions.Select(v => v.Key).OrderByDescending(v => v, NpmVersionComparer.Instance).ToList();
            version ??= metadata["dist-tags"]?["latest"]?.GetValue<string>() ?? ordered.FirstOrDefault();
            if (version == null || versions[version] is not JsonObject selected) throw new FileNotFoundException();
            var tags = metadata["dist-tags"] as JsonObject ?? new JsonObject();
            var author = selected["author"];
            var repository = selected["repository"];
            var repoUrl = PackageReadme.WebUrl(repository is JsonObject repo ? Text(repo["url"]) : Text(repository));
            var readme = "";
            if (tab is not ("versions" or "dependencies" or "usedby"))
            {
                readme = Text(selected["readme"]);
                if (string.IsNullOrWhiteSpace(readme) && Text(metadata["dist-tags"]?["latest"]) == version) readme = Text(metadata["readme"]);
                if (string.IsNullOrWhiteSpace(readme)) readme = await registry.CachedReadmeAsync(id, version, cancellationToken);
            }
            var dependencies = new List<PackageDependency>();
            foreach (var kind in new[] { "dependencies", "peerDependencies", "optionalDependencies", "devDependencies" })
                if (selected[kind] is JsonObject group)
                    dependencies.AddRange(group.Select(d => new PackageDependency(d.Key, Text(d.Value), kind)));
            return this.StackView(new DetailsViewModel
            {
                PageTitle = id, Name = id, Version = version, Query = q, Page = Math.Max(1, page),
                VersionsPage = Math.Clamp(versionsPage, 1, Math.Max(1, (ordered.Count + 19) / 20)), DependentsPage = Math.Clamp(dependentsPage, 1, int.MaxValue / 20),
                ActiveTab = tab is "versions" or "dependencies" or "usedby" ? tab : "overview",
                IsLocallyPublished = await localPackages.FindAsync(id, cancellationToken) != null,
                Description = Text(selected["description"] ?? metadata["description"]), License = Text(selected["license"]),
                Author = author is JsonObject person ? Text(person["name"]) : Text(author),
                Homepage = PackageReadme.WebUrl(Text(selected["homepage"])), Repository = repoUrl,
                ReadmeHtml = PackageReadme.Render(readme, repoUrl),
                Deprecated = Text(selected["deprecated"]), Published = Date(metadata["time"]?[version]), Dependencies = dependencies,
                Versions = ordered.Select(v => new PackageVersion(v, Date(metadata["time"]?[v]),
                    tags.Where(t => Text(t.Value) == v).Select(t => t.Key).ToList(), !string.IsNullOrEmpty(Text(versions[v]?["deprecated"])))).ToList(),
                Dependents = tab == "usedby" ? await registry.DependentsAsync(id, cancellationToken) : []
            });
        }
        catch (Exception ex) when (ex is ArtifactDeniedException or AdmissionUnavailableException or FileNotFoundException or ArgumentException or HttpRequestException or InvalidDataException)
        {
            var status = ex switch { ArtifactDeniedException => 403, AdmissionUnavailableException => 503,
                FileNotFoundException => 404, ArgumentException => 400, _ => 502 };
            Response.StatusCode = status;
            return this.StackView(new DetailsViewModel { Name = id, Query = q, Page = Math.Max(1, page), ErrorStatus = status });
        }
    }
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static DateTimeOffset? Date(JsonNode? node) => DateTimeOffset.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
