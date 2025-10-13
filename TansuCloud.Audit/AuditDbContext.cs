// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Microsoft.EntityFrameworkCore;

namespace TansuCloud.Audit;

/// <summary>
/// EF Core DbContext for audit_events persistence in the shared tansu_audit database.
/// This database is shared across all tenants and services for centralized audit logging.
/// </summary>
public sealed class AuditDbContext : DbContext
{
    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Audit events table with time-series optimizations for high-volume logging.
    /// </summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            // Primary key on id (UUID v7 provides natural time-ordering)
            entity.HasKey(e => e.Id);

            // Indexes for common query patterns
            // Time-range queries (most common pattern for audit retrieval)
            entity.HasIndex(e => e.WhenUtc)
                .HasDatabaseName("ix_audit_events_when_utc");

            // Tenant-scoped queries
            entity.HasIndex(e => e.TenantId)
                .HasDatabaseName("ix_audit_events_tenant_id");

            // Category/action filtering
            entity.HasIndex(e => e.Category)
                .HasDatabaseName("ix_audit_events_category");
            
            entity.HasIndex(e => e.Action)
                .HasDatabaseName("ix_audit_events_action");

            // Service-level audit trails
            entity.HasIndex(e => e.Service)
                .HasDatabaseName("ix_audit_events_service");

            // Subject (user) activity tracking
            entity.HasIndex(e => e.Subject)
                .HasDatabaseName("ix_audit_events_subject");

            // Distributed tracing correlation
            entity.HasIndex(e => e.CorrelationId)
                .HasDatabaseName("ix_audit_events_correlation_id");

            entity.HasIndex(e => e.TraceId)
                .HasDatabaseName("ix_audit_events_trace_id");

            // Composite index for tenant + time (most common query: tenant audit logs by time range)
            entity.HasIndex(e => new { e.TenantId, e.WhenUtc })
                .HasDatabaseName("ix_audit_events_tenant_when");

            // Composite index for service + time (service-level audit reports)
            entity.HasIndex(e => new { e.Service, e.WhenUtc })
                .HasDatabaseName("ix_audit_events_service_when");

            // JSONB GIN index for metadata queries (PostgreSQL-specific)
            // This allows efficient queries like: WHERE metadata @> '{"key": "value"}'
            entity.HasIndex(e => e.Metadata)
                .HasDatabaseName("ix_audit_events_metadata_gin")
                .HasMethod("gin");
        });
    } // End of OnModelCreating
} // End of Class AuditDbContext
