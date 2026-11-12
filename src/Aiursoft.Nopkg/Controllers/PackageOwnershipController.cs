using Aiursoft.Nopkg.Authorization;
using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Registry;
using Aiursoft.Nopkg.Models.PackageOwnershipViewModels;
using Aiursoft.Nopkg.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using EFCoreSecondLevelCacheInterceptor;

namespace Aiursoft.Nopkg.Controllers;

[Authorize(Policy = AppPermissionNames.CanManagePackages)]
public class PackageOwnershipController(NopkgDbContext db, NpmRegistry registry, ILocalPackageStore localPackages) : Controller
{
    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1,
        CascadedLinksGroupName = "Packages", CascadedLinksIcon = "package", CascadedLinksOrder = 1,
        LinkText = "Package ownership", LinkOrder = 3)]
    public async Task<IActionResult> Index(string q = "", bool unassignedOnly = false, int page = 1)
    {
        page = Math.Clamp(page, 1, int.MaxValue / 20);
        var query = db.PackageFamilies.AsNoTracking().NotCacheable().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var normalized = q.ToLowerInvariant();
            query = query.Where(r => r.Name.Contains(normalized));
        }
        if (unassignedOnly) query = query.Where(r => r.OwnerUserId == null);
        return this.StackView(new IndexViewModel
        {
            PageTitle = "Package ownership", Query = q, UnassignedOnly = unassignedOnly, Page = page,
            Total = await query.CountAsync(),
            Items = await query.OrderBy(r => r.Name).Skip((page - 1) * 20).Take(20)
                .Select(r => new OwnershipItem
                {
                    Id = r.Id, PackageId = r.Name,
                    IsLocallyPublished = r.IsLocallyPublished,
                    OwnerName = r.OwnerUser == null ? null : r.OwnerUser.DisplayName,
                    VersionCount = r.Versions.Count()
                }).ToListAsync()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        foreach (var name in await registry.CachedMirrorNamesAsync(cancellationToken))
            await localPackages.RecordMirrorAsync(name, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Assign(int id, string q = "", bool unassignedOnly = false, int page = 1)
    {
        var registration = await db.PackageFamilies.NotCacheable().SingleOrDefaultAsync(x => x.Id == id);
        if (registration == null) return NotFound();
        var model = new AssignViewModel
        {
            PageTitle = "Assign package owner", Id = id, PackageId = registration.Name,
            OwnerUserId = registration.OwnerUserId, PreviousOwnerUserId = registration.OwnerUserId,
            Revision = registration.Revision, IsMirrored = !registration.IsLocallyPublished,
            Query = q, UnassignedOnly = unassignedOnly, Page = Math.Max(1, page)
        };
        await PopulateUsersAsync(model);
        return this.StackView(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, AssignViewModel model)
    {
        var registration = await db.PackageFamilies.NotCacheable().SingleOrDefaultAsync(x => x.Id == id);
        if (registration == null) return NotFound();
        if (registration.Revision != model.Revision)
        {
            ModelState.AddModelError(string.Empty, "The owner has changed. Review the current owner and try again.");
            model.OwnerUserId = registration.OwnerUserId;
            ModelState.Remove(nameof(model.OwnerUserId));
            Response.StatusCode = 409;
        }
        if (!string.IsNullOrEmpty(model.OwnerUserId) && !await db.Users.AnyAsync(u => u.Id == model.OwnerUserId))
            ModelState.AddModelError(nameof(model.OwnerUserId), "Choose an existing user.");
        if (!registration.IsLocallyPublished && !string.IsNullOrEmpty(model.OwnerUserId) && !model.ConfirmConversion)
            ModelState.AddModelError(nameof(model.ConfirmConversion), "Confirm that this package will stop mirroring and use internally published versions.");
        if (!ModelState.IsValid)
        {
            model.Id = id;
            model.PackageId = registration.Name;
            model.PageTitle = "Assign package owner";
            model.PreviousOwnerUserId = registration.OwnerUserId;
            model.Revision = registration.Revision;
            model.IsMirrored = !registration.IsLocallyPublished;
            ModelState.Remove(nameof(model.Revision));
            ModelState.Remove(nameof(model.PreviousOwnerUserId));
            await PopulateUsersAsync(model);
            return this.StackView(model);
        }
        registration.OwnerUserId = string.IsNullOrEmpty(model.OwnerUserId) ? null : model.OwnerUserId;
        if (registration.OwnerUserId != null) registration.IsLocallyPublished = true;
        registration.Revision = Guid.NewGuid().ToString();
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Conflict("Package ownership changed. Reload the current package."); }
        return RedirectToAction(nameof(Index), new { q = model.Query, model.UnassignedOnly, model.Page });
    }

    private async Task PopulateUsersAsync(AssignViewModel model) => model.Users = await db.Users
        .AsNoTracking().OrderBy(u => u.DisplayName)
        .Select(u => new SelectListItem { Value = u.Id, Text = u.DisplayName == u.UserName ? u.DisplayName : u.DisplayName + " (" + u.UserName + ")" }).ToListAsync();
}
