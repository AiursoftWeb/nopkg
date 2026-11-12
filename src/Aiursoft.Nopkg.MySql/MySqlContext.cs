using System.Diagnostics.CodeAnalysis;
using Aiursoft.Nopkg.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.MySql;

[ExcludeFromCodeCoverage]

public class MySqlContext(DbContextOptions<MySqlContext> options) : NopkgDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // npm prerelease identifiers are case-sensitive.
        builder.Entity<PublishedPackage>().Property(x => x.Version).UseCollation("utf8mb4_bin");
    }
}
