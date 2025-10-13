// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TansuCloud.Audit;

/// <summary>
/// Design-time factory for EF Core migrations tooling.
/// Uses a local development connection string; production uses injected options.
/// </summary>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuditDbContext>();

        // Development connection string for migrations
        // Production uses configuration-driven connection string via DI
        optionsBuilder.UseNpgsql(
            "Host=localhost;Port=5432;Database=tansu_audit;Username=postgres;Password=postgres",
            npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "public");
            }
        );

        return new AuditDbContext(optionsBuilder.Options);
    } // End of CreateDbContext
} // End of Class AuditDbContextFactory
