using Aiursoft.Nopkg.Models.DashboardViewModels;
using Aiursoft.Nopkg.Registry;
using Aiursoft.Nopkg.Services;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.Nopkg.Controllers;

[LimitPerMin]
public class DashboardController(NpmRegistry registry, RegistryOptions options, ILocalPackageStore localPackages) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Features",
        NavGroupOrder = 1,
        CascadedLinksGroupName = "Registry",
        CascadedLinksIcon = "package",
        CascadedLinksOrder = 1,
        LinkText = "Packages",
        LinkOrder = 1)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken, string q = "", int page = 1)
    {
        Response.Headers.CacheControl = "no-store";
        q = q.Trim();
        page = Math.Clamp(page, 1, int.MaxValue / 20);
        try
        {
            var packages = await registry.CachedPackagesAsync(cancellationToken);
            var filtered = Filter(packages, q);
            return this.StackView(new IndexViewModel
            {
                Packages = filtered.Skip((page - 1) * 20).Take(20).ToList(),
                Query = q, Page = page, TotalHits = filtered.Count, TotalPackages = packages.Count,
                CacheOnly = options.CacheOnly, AdmissionEnabled = options.S360.Enabled
            });
        }
        catch (AdmissionUnavailableException)
        {
            var internalPackages = await localPackages.ListAsync(cancellationToken);
            var filtered = Filter(internalPackages, q);
            Response.StatusCode = internalPackages.Count > 0 ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            return this.StackView(new IndexViewModel
            {
                Packages = filtered.Skip((page - 1) * 20).Take(20).ToList(),
                Query = q, Page = page, TotalHits = filtered.Count, TotalPackages = internalPackages.Count,
                CacheOnly = options.CacheOnly, AdmissionEnabled = options.S360.Enabled, AdmissionUnavailable = true
            });
        }
    }
    private static IReadOnlyList<CachedPackage> Filter(IReadOnlyList<CachedPackage> packages, string query) =>
        packages.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
