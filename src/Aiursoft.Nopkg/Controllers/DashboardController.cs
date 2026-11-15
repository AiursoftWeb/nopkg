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
    public async Task<IActionResult> Index(CancellationToken cancellationToken, string q = "", int page = 1, string? after = null, string? before = null)
    {
        Response.Headers.CacheControl = "no-store";
        q = q.Trim();
        page = Math.Clamp(page, 1, int.MaxValue / 20);
        try
        {
            var result = await registry.CatalogPageAsync(q, page, cancellationToken, after, before);
            var visible = new List<CachedPackage>();
            foreach (var package in result.Packages)
                visible.Add(await registry.DescribeAsync(package, cancellationToken));
            return this.StackView(new IndexViewModel
            {
                Packages = visible,
                Query = q, Page = page, After = after, Before = before, HasPrevious = result.HasPrevious, HasNext = result.HasNext, TotalPackages = result.KnownPackages,
                CacheOnly = options.CacheOnly, AdmissionEnabled = options.S360.Enabled
            });
        }
        catch (AdmissionUnavailableException)
        {
            var internalPackages = await localPackages.ListAsync(cancellationToken);
            var backward = !string.IsNullOrEmpty(before);
            var seek = backward || !string.IsNullOrEmpty(after);
            var filtered = Filter(internalPackages, q).Where(p => !seek || (backward
                ? StringComparer.Ordinal.Compare(p.Name, before) < 0
                : StringComparer.Ordinal.Compare(p.Name, after) > 0));
            filtered = backward ? filtered.Reverse() : filtered;
            var candidates = filtered.Skip(seek ? 0 : (page - 1) * 20).Take(21).ToList();
            var visible = new List<CachedPackage>();
            foreach (var package in candidates.Take(20))
                visible.Add(await registry.DescribeAsync(package, cancellationToken));
            if (backward) visible.Reverse();
            Response.StatusCode = internalPackages.Count > 0 ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            return this.StackView(new IndexViewModel
            {
                Packages = visible,
                Query = q, Page = page, After = after, Before = before, HasNext = backward || candidates.Count > 20, HasPrevious = backward ? candidates.Count > 20 : seek || page > 1, TotalPackages = internalPackages.Count,
                CacheOnly = options.CacheOnly, AdmissionEnabled = options.S360.Enabled, AdmissionUnavailable = true
            });
        }
    }
    private static IReadOnlyList<CachedPackage> Filter(IReadOnlyList<CachedPackage> packages, string query) =>
        packages.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
