// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TansuCloud.Database.EF;

public sealed class TansuDbContextFactory : IDesignTimeDbContextFactory<TansuDbContext>
{
    public TansuDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("TANSU_DESIGN_TIME_CS")
                 ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
        var builder = new DbContextOptionsBuilder<TansuDbContext>()
            .UseNpgsql(cs);
        return new TansuDbContext(builder.Options);
    }
} // End of Class TansuDbContextFactory
