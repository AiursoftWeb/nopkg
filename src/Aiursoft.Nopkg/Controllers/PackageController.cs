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
    public async Task<IActionResult> Details(string id, string? version = null, string q = "", int page = 1, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var metadata = await registry.MetadataAsync(id, cancellationToken);
            var versions = metadata["versions"] as JsonObject ?? new JsonObject();
            version ??= metadata["dist-tags"]?["latest"]?.GetValue<string>() ?? versions.FirstOrDefault().Key;
            if (version == null || versions[version] is not JsonObject selected) return NotFound();
            return this.StackView(new DetailsViewModel
            {
                Name = id, Version = version, Query = q, Page = Math.Max(1, page),
                IsLocallyPublished = await localPackages.FindAsync(id, cancellationToken) != null,
                Description = selected["description"]?.GetValue<string>() ?? metadata["description"]?.GetValue<string>() ?? string.Empty,
                License = selected["license"] is JsonValue license && license.TryGetValue<string>(out var value) ? value : string.Empty,
                Dependencies = selected["dependencies"] is JsonObject dependencies
                    ? dependencies.ToDictionary(d => d.Key, d => d.Value?.GetValue<string>() ?? string.Empty)
                    : new Dictionary<string, string>(),
                Versions = versions.Select(v => v.Key).Reverse().ToList()
            });
        }
        catch (Exception ex) when (ex is ArtifactDeniedException or AdmissionUnavailableException or FileNotFoundException or ArgumentException or HttpRequestException or InvalidDataException)
        {
            var status = ex switch
            {
                ArtifactDeniedException => 403,
                AdmissionUnavailableException => 503,
                FileNotFoundException => 404,
                ArgumentException => 400,
                _ => 502
            };
            Response.StatusCode = status;
            return this.StackView(new DetailsViewModel { Name = id, Query = q, Page = Math.Max(1, page), ErrorStatus = status });
        }
    }
}
