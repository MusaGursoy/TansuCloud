// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;

namespace TansuCloud.Gateway.Data;

/// <summary>
/// DbContext for Gateway policies stored in PostgreSQL.
/// Shares the Identity database connection string.
/// </summary>
public class PolicyDbContext : DbContext
{
    public PolicyDbContext(DbContextOptions<PolicyDbContext> options)
        : base(options)
    {
    } // End of Constructor PolicyDbContext

    /// <summary>Gateway policies table.</summary>
    public DbSet<PolicyEntity> Policies { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PolicyEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            // Indexes for common queries
            entity.HasIndex(e => e.Type)
                .HasDatabaseName("ix_gateway_policies_type");
            
            entity.HasIndex(e => e.Enabled)
                .HasDatabaseName("ix_gateway_policies_enabled");
            
            entity.HasIndex(e => new { e.Type, e.Enabled })
                .HasDatabaseName("ix_gateway_policies_type_enabled");
        });
    } // End of Method OnModelCreating
} // End of Class PolicyDbContext
