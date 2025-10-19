// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TansuCloud.Gateway.Data;

/// <summary>
/// Design-time factory for PolicyDbContext to enable EF Core migrations.
/// </summary>
public class PolicyDbContextFactory : IDesignTimeDbContextFactory<PolicyDbContext>
{
    public PolicyDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PolicyDbContext>();
        
        // Use default dev connection string for migrations
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=tansu_identity;Username=postgres;Password=postgres");

        return new PolicyDbContext(optionsBuilder.Options);
    } // End of Method CreateDbContext
} // End of Class PolicyDbContextFactory
