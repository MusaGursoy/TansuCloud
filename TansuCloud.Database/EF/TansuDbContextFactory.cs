// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TansuCloud.Database.EF;

/// <summary>
/// Design-time factory for TansuDbContext to enable EF Core tools (migrations, etc.)
/// </summary>
public class TansuDbContextFactory : IDesignTimeDbContextFactory<TansuDbContext>
{
    public TansuDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TansuDbContext>();
        
        // Use a dummy connection string - migrations don't need a real DB connection
        optionsBuilder.UseNpgsql("Host=localhost;Database=tansu_tenant_design;Username=postgres;Password=postgres");
        
        return new TansuDbContext(optionsBuilder.Options);
    }
}
