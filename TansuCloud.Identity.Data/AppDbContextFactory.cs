// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TansuCloud.Identity.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        var cs = Environment.GetEnvironmentVariable("TANSU_IDENTITY_CS")
            ?? "Host=localhost;Port=5432;Database=tansu_identity;Username=postgres;Password=postgres";
        optionsBuilder.UseNpgsql(cs);
        optionsBuilder.UseOpenIddict();
        return new AppDbContext(optionsBuilder.Options);
    }
}
