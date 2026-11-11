using Aiursoft.Nopkg.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.InMemory;

public class InMemoryContext(DbContextOptions<InMemoryContext> options) : NopkgDbContext(options)
{
    public override Task MigrateAsync(CancellationToken cancellationToken)
    {
        return Database.EnsureCreatedAsync(cancellationToken);
    }

    public override Task<bool> CanConnectAsync()
    {
        return Task.FromResult(true);
    }
}
