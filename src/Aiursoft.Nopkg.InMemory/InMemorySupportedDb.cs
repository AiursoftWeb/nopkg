using Aiursoft.DbTools;
using Aiursoft.DbTools.InMemory;
using Aiursoft.Nopkg.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.Nopkg.InMemory;

public class InMemorySupportedDb : SupportedDatabaseType<NopkgDbContext>
{
    public override string DbType => "InMemory";

    public override IServiceCollection RegisterFunction(IServiceCollection services, string connectionString)
    {
        return services.AddAiurInMemoryDb<InMemoryContext>();
    }

    public override NopkgDbContext ContextResolver(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<InMemoryContext>();
    }
}
