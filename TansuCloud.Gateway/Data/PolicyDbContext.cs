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

    /// <summary>Observability settings table (retention, sampling).</summary>
    public DbSet<ObservabilitySettings> ObservabilitySettings { get; set; } = null!;

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

        modelBuilder.Entity<ObservabilitySettings>(entity =>
        {
            entity.ToTable("observability_settings");
            entity.HasKey(e => e.Id);
            
            // Map properties to snake_case columns
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Component).HasColumnName("component");
            entity.Property(e => e.RetentionDays).HasColumnName("retention_days");
            entity.Property(e => e.SamplingPercent).HasColumnName("sampling_percent");
            entity.Property(e => e.Enabled).HasColumnName("enabled");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            
            // Unique constraint on Component
            entity.HasIndex(e => e.Component)
                .IsUnique()
                .HasDatabaseName("ix_observability_settings_component");
            
            // Index for enabled components
            entity.HasIndex(e => e.Enabled)
                .HasDatabaseName("ix_observability_settings_enabled");
        });

        // Seed default settings for PGTL components (static dates to avoid pending model changes warning)
        modelBuilder.Entity<ObservabilitySettings>().HasData(
            new ObservabilitySettings
            {
                Id = 1,
                Component = "prometheus",
                RetentionDays = 7,
                SamplingPercent = 100,
                Enabled = true,
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedBy = null
            },
            new ObservabilitySettings
            {
                Id = 2,
                Component = "tempo",
                RetentionDays = 7,
                SamplingPercent = 100,
                Enabled = true,
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedBy = null
            },
            new ObservabilitySettings
            {
                Id = 3,
                Component = "loki",
                RetentionDays = 7,
                SamplingPercent = 100,
                Enabled = true,
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedBy = null
            }
        );
    } // End of Method OnModelCreating
} // End of Class PolicyDbContext
