using System.Security.Cryptography;
using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Models.UserApiKeysViewModels;
using Aiursoft.Nopkg.Services.Authentication;
using Aiursoft.Nopkg.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class UserApiKeysController(NopkgDbContext db, UserManager<User> users, 
    PackagePublisherAuthenticationService publishers) : Controller
{
    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998,
        CascadedLinksGroupName = "Personal", CascadedLinksIcon = "user-circle", CascadedLinksOrder = 10,
        LinkText = "API Keys", LinkOrder = 10)]
    public async Task<IActionResult> Index() => this.StackView(new IndexViewModel
    {
        PageTitle = "API Keys",
        Keys = await db.UserApiKeys.AsNoTracking().Where(k => k.UserId == users.GetUserId(User))
            .OrderByDescending(k => k.CreatedAt).ToListAsync()
    });

    [HttpGet]
    public IActionResult Create() => this.StackView(new CreateViewModel { PageTitle = "New API key" });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.PageTitle = "New API key";
            return this.StackView(model);
        }
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var key = new UserApiKey
        {
            UserId = users.GetUserId(User)!, Name = model.Name.Trim(),
            KeyHash = PackagePublisherAuthenticationService.ComputeSha256Hex(raw), KeyPrefix = raw[..8],
            ExpiresAt = model.ExpirationDays > 0 ? DateTime.UtcNow.AddDays(model.ExpirationDays) : null
        };
        db.UserApiKeys.Add(key);
        await db.SaveChangesAsync();
        TempData[$"NewApiKey:{key.Id}"] = raw;
        return RedirectToAction(nameof(Usage), new { id = key.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Usage(int id)
    {
        var key = await FindKeyAsync(id);
        if (key == null) return NotFound();
        var user = await users.GetUserAsync(User);
        if (user == null) return Challenge();
        var publisher = await publishers.ForUserAsync(user);
        return this.StackView(new UsageViewModel
        {
            PageTitle = "Use API key", Key = key, RawKey = TempData[$"NewApiKey:{id}"] as string,
            RegistryUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/npm/",
            CanPublish = publisher.CanPublish || publisher.CanManagePackages
        });
    }

    [HttpGet]
    public async Task<IActionResult> Delete(int id)
    {
        var key = await FindKeyAsync(id);
        return key == null ? NotFound() : this.StackView(new DeleteViewModel { PageTitle = "Revoke API key", Key = key });
    }

    [HttpPost, ActionName(nameof(Delete)), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var key = await FindKeyAsync(id);
        if (key == null) return NotFound();
        db.UserApiKeys.Remove(key);
        await db.SaveChangesAsync();
        TempData["SuccessMessage"] = "API key revoked.";
        return RedirectToAction(nameof(Index));
    }

    private Task<UserApiKey?> FindKeyAsync(int id) =>
        db.UserApiKeys.SingleOrDefaultAsync(k => k.Id == id && k.UserId == users.GetUserId(User));
}
