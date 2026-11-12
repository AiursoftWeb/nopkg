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
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return this.StackView(new IndexViewModel
            {
                Packages = await registry.CachedPackagesAsync(cancellationToken),
                CacheOnly = options.CacheOnly, AdmissionEnabled = options.S360.Enabled
            });
        }
        catch (AdmissionUnavailableException)
        {
            var internalPackages = await localPackages.ListAsync(cancellationToken);
            Response.StatusCode = internalPackages.Count > 0 ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            return this.StackView(new IndexViewModel
            {
                Packages = internalPackages,
                CacheOnly = options.CacheOnly, AdmissionEnabled = options.S360.Enabled, AdmissionUnavailable = true
            });
        }
    }
}
