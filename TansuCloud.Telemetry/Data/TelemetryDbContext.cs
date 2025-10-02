// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using TansuCloud.Telemetry.Data.Entities;

namespace TansuCloud.Telemetry.Data;

/// <summary>
/// Entity Framework Core database context for the telemetry ingestion service.
/// </summary>
public sealed class TelemetryDbContext : DbContext
{
    public TelemetryDbContext(DbContextOptions<TelemetryDbContext> options)
        : base(options)
    {
    } // End of Constructor TelemetryDbContext

    public DbSet<TelemetryEnvelopeEntity> Envelopes => Set<TelemetryEnvelopeEntity>(); // End of Property Envelopes

    public DbSet<TelemetryItemEntity> Items => Set<TelemetryItemEntity>(); // End of Property Items

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TelemetryEnvelopeEntity>(entity =>
        {
            entity.ToTable("telemetry_envelopes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Host).IsRequired();
            entity.Property(e => e.Environment).IsRequired();
            entity.Property(e => e.Service).IsRequired();
            entity.Property(e => e.SeverityThreshold).IsRequired();
            entity.Property(e => e.WindowMinutes).IsRequired();
            entity.Property(e => e.MaxItems).IsRequired();
            entity.Property(e => e.ItemCount).IsRequired();
            entity.Property(e => e.ReceivedAtUtc).IsRequired();

            entity.HasIndex(e => e.ReceivedAtUtc).HasDatabaseName("IX_envelopes_received_at");
            entity.HasIndex(e => e.Service).HasDatabaseName("IX_envelopes_service");
            entity.HasIndex(e => e.Environment).HasDatabaseName("IX_envelopes_environment");
        });

        modelBuilder.Entity<TelemetryItemEntity>(entity =>
        {
            entity.ToTable("telemetry_items");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.TimestampUtc).IsRequired();
            entity.Property(e => e.Level).IsRequired();
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.TemplateHash).IsRequired();
            entity.Property(e => e.Count).IsRequired();
            entity.Property(e => e.PropertiesJson).HasColumnType("TEXT");

            entity.HasIndex(e => e.EnvelopeId).HasDatabaseName("IX_items_envelope_id");
            entity.HasIndex(e => e.TimestampUtc).HasDatabaseName("IX_items_timestamp");
            entity.HasIndex(e => e.Level).HasDatabaseName("IX_items_level");

            entity.HasOne(e => e.Envelope)
                .WithMany(e => e.Items)
                .HasForeignKey(e => e.EnvelopeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    } // End of Method OnModelCreating
} // End of Class TelemetryDbContext
