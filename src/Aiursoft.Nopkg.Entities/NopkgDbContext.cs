using System.Diagnostics.CodeAnalysis;
using Aiursoft.DbTools;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.Entities;

[ExcludeFromCodeCoverage]

public abstract class NopkgDbContext(DbContextOptions options) : IdentityDbContext<User>(options), ICanMigrate
{
    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<PackageFamily> PackageFamilies => Set<PackageFamily>();
    public DbSet<PublishedPackage> PublishedPackages => Set<PublishedPackage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PackageFamily>().HasOne(x => x.OwnerUser).WithMany(x => x.OwnedPackages)
            .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<PublishedPackage>().HasOne(x => x.UploadedByUser).WithMany(x => x.PublishedPackages)
            .HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<UserApiKey>().HasOne(x => x.User).WithMany(x => x.ApiKeys)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}
