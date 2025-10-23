// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TansuCloud.Identity.Data.Entities;

namespace TansuCloud.Identity.Data;

public sealed class AppDbContext : IdentityDbContext<IdentityUser, IdentityRole, string>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<ExternalProviderSetting> ExternalProviderSettings =>
        Set<ExternalProviderSetting>();
    public DbSet<JwkKey> JwkKeys => Set<JwkKey>();
} // End of Class AppDbContext
