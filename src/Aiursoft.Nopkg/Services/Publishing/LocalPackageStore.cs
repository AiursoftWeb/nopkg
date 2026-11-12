using System.Text.Json.Nodes;
using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Registry;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;
namespace Aiursoft.Nopkg.Services.Publishing;

public sealed class LocalPackageStore(IServiceScopeFactory scopes) : ILocalPackageStore
{
    public async Task<LocalPackage?> FindAsync(string name, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
        var family = await db.PackageFamilies.AsNoTracking().NotCacheable().SingleOrDefaultAsync(x => x.Name == name, token);
        if (family is not { IsLocallyPublished: true }) return null;
        var packages = await db.PublishedPackages.AsNoTracking().NotCacheable().Where(x => x.FamilyId == family.Id)
            .OrderBy(x => x.PublishedAt).ToListAsync(token);
        var versions = new JsonObject();var times = new JsonObject();var hashes = new Dictionary<string,string>();
        foreach (var package in packages)
        {
            versions[package.Version] = JsonNode.Parse(package.ManifestJson);
            times[package.Version] = DateTime.SpecifyKind(package.PublishedAt, DateTimeKind.Utc).ToString("O");
            hashes[package.Version] = package.Sha256;
        }
        return new LocalPackage(new JsonObject { ["name"] = name, ["versions"] = versions,
            ["dist-tags"] = JsonNode.Parse(family.TagsJson), ["time"] = times }, hashes);
    }

    public async Task<bool> RecordMirrorAsync(string name, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
        var family = await db.PackageFamilies.AsNoTracking().NotCacheable().SingleOrDefaultAsync(x => x.Name == name, token);
        if (family != null) return !family.IsLocallyPublished;
        db.PackageFamilies.Add(new PackageFamily { Name = name });
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.PackageFamilies.AsNoTracking().NotCacheable().SingleOrDefaultAsync(x => x.Name == name, token);
            if (winner == null) throw;
            return !winner.IsLocallyPublished;
        }
    }

    public async Task<IReadOnlyList<CachedPackage>> ListAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NopkgDbContext>();
        return await db.PackageFamilies.AsNoTracking().NotCacheable().Where(x => x.IsLocallyPublished)
            .Select(x => new CachedPackage(x.Name, x.Versions.Count(), x.Versions.Count(), true)).ToListAsync(token);
    }
}
