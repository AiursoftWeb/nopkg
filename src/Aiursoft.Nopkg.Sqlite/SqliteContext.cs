using System.Diagnostics.CodeAnalysis;
using Aiursoft.Nopkg.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.Sqlite;

[ExcludeFromCodeCoverage]

public class SqliteContext(DbContextOptions<SqliteContext> options) : NopkgDbContext(options)
{
    public override Task<bool> CanConnectAsync()
    {
        return Task.FromResult(true);
    }
}
