using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.Entities;

public static class ApiKeyStatistics
{
    public static async Task<bool> RecordUseAsync(this NopkgDbContext db, int keyId, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var keys = db.UserApiKeys.Where(k => k.Id == keyId && (!k.ExpiresAt.HasValue || k.ExpiresAt > now));
        if (db.Database.IsRelational())
        {
            return await keys.ExecuteUpdateAsync(update => update
                .SetProperty(k => k.TotalUses, k => k.TotalUses + 1)
                .SetProperty(k => k.LastUsedAt, k => k.LastUsedAt == null || k.LastUsedAt < now ? now : k.LastUsedAt), token) == 1;
        }

        var key = await keys.SingleOrDefaultAsync(token);
        if (key == null) return false;
        key.TotalUses++;
        key.LastUsedAt = now;
        await db.SaveChangesAsync(token);
        return true;
    }

    public static async Task RecordPublicationAsync(this NopkgDbContext db, int? keyId,
        string name, string version, CancellationToken token)
    {
        if (!keyId.HasValue) return;
        var now = DateTime.UtcNow;
        var keys = db.UserApiKeys.Where(k => k.Id == keyId);
        if (db.Database.IsRelational())
        {
            await keys.ExecuteUpdateAsync(update => update
                .SetProperty(k => k.PublishedPackagesCount, k => k.PublishedPackagesCount + 1)
                .SetProperty(k => k.LastPublishedPackage, k => k.LastPublishedAt == null || k.LastPublishedAt <= now ? name : k.LastPublishedPackage)
                .SetProperty(k => k.LastPublishedVersion, k => k.LastPublishedAt == null || k.LastPublishedAt <= now ? version : k.LastPublishedVersion)
                .SetProperty(k => k.LastPublishedAt, k => k.LastPublishedAt == null || k.LastPublishedAt <= now ? now : k.LastPublishedAt), token);
            return;
        }

        var key = await keys.SingleOrDefaultAsync(token);
        if (key == null) return;
        key.PublishedPackagesCount++;
        key.LastPublishedAt = now;
        key.LastPublishedPackage = name;
        key.LastPublishedVersion = version;
        await db.SaveChangesAsync(token);
    }
}
