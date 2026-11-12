using System.Security.Cryptography;
using System.Text;
using Aiursoft.Nopkg.Authorization;
using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Services.Publishing;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;
namespace Aiursoft.Nopkg.Services.Authentication;

public sealed class PackagePublisherAuthenticationService(NopkgDbContext db)
{
    public static string ComputeSha256Hex(string rawKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    public async Task<PackagePublisher?> AuthenticateAsync(string rawKey, CancellationToken token)
    {
        if (string.IsNullOrEmpty(rawKey) || rawKey.Length > 256) return null;
        var hash = ComputeSha256Hex(rawKey);
        var key = await db.UserApiKeys.AsNoTracking().Include(x => x.User).NotCacheable()
            .SingleOrDefaultAsync(x => x.KeyHash == hash, token);
        if (key?.User == null || key.IsExpired || key.User.LockoutEnd > DateTimeOffset.UtcNow) return null;
        var publisher = await ForUserAsync(key.User);
        var tracked = db.ChangeTracker.Entries<UserApiKey>().SingleOrDefault(x => x.Entity.Id == key.Id);
        if (tracked != null) tracked.State = EntityState.Detached;
        key.User = null;
        db.Entry(key).State = EntityState.Unchanged;
        key.LastUsedAt = DateTime.UtcNow;
        db.Entry(key).Property(x => x.LastUsedAt).IsModified = true;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return null; }
        return publisher;
    }

    public async Task<PackagePublisher> ForUserAsync(User user)
    {
        // Read current membership and permissions; query caching must not delay revocation.
        var roles = from membership in db.UserRoles
                    join claim in db.RoleClaims on membership.RoleId equals claim.RoleId
                    where membership.UserId == user.Id && claim.ClaimType == AppPermissions.Type
                    select claim.ClaimValue;
        var permissions = await roles.NotCacheable().ToListAsync();
        permissions.AddRange(await db.UserClaims.NotCacheable().Where(x => x.UserId == user.Id && x.ClaimType == AppPermissions.Type)
            .Select(x => x.ClaimValue).ToListAsync());
        return new PackagePublisher(user.Id, permissions.Contains(AppPermissionNames.CanPublishPackages),
            permissions.Contains(AppPermissionNames.CanManagePackages), user.UserName);
    }
}
