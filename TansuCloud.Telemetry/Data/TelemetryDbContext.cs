// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TansuCloud.Telemetry.Data.Records;

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

    public DbSet<TelemetryActiveEnvelopeRecord> ActiveEnvelopes =>
        Set<TelemetryActiveEnvelopeRecord>(); // End of Property ActiveEnvelopes

    public DbSet<TelemetryArchivedEnvelopeRecord> ArchivedEnvelopes =>
        Set<TelemetryArchivedEnvelopeRecord>(); // End of Property ArchivedEnvelopes

    public DbSet<TelemetryActiveItemRecord> ActiveItems =>
        Set<TelemetryActiveItemRecord>(); // End of Property ActiveItems

    public DbSet<TelemetryArchivedItemRecord> ArchivedItems =>
        Set<TelemetryArchivedItemRecord>(); // End of Property ArchivedItems

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureEnvelopeSet<TelemetryActiveEnvelopeRecord, TelemetryActiveItemRecord>(
            modelBuilder.Entity<TelemetryActiveEnvelopeRecord>(),
            tableName: "telemetry_envelopes_active",
            receivedIndexName: "IX_active_envelopes_received_at",
            serviceIndexName: "IX_active_envelopes_service",
            environmentIndexName: "IX_active_envelopes_environment",
            acknowledgedIndexName: "IX_active_envelopes_acknowledged_at",
            deletedIndexName: "IX_active_envelopes_deleted_at"
        );

        ConfigureEnvelopeSet<TelemetryArchivedEnvelopeRecord, TelemetryArchivedItemRecord>(
            modelBuilder.Entity<TelemetryArchivedEnvelopeRecord>(),
            tableName: "telemetry_envelopes_archive",
            receivedIndexName: "IX_archived_envelopes_received_at",
            serviceIndexName: "IX_archived_envelopes_service",
            environmentIndexName: "IX_archived_envelopes_environment",
            acknowledgedIndexName: "IX_archived_envelopes_acknowledged_at",
            deletedIndexName: "IX_archived_envelopes_deleted_at"
        );

        ConfigureItemSet<TelemetryActiveItemRecord, TelemetryActiveEnvelopeRecord>(
            modelBuilder.Entity<TelemetryActiveItemRecord>(),
            tableName: "telemetry_items_active",
            envelopeIdIndexName: "IX_active_items_envelope_id",
            timestampIndexName: "IX_active_items_timestamp",
            levelIndexName: "IX_active_items_level"
        );

        ConfigureItemSet<TelemetryArchivedItemRecord, TelemetryArchivedEnvelopeRecord>(
            modelBuilder.Entity<TelemetryArchivedItemRecord>(),
            tableName: "telemetry_items_archive",
            envelopeIdIndexName: "IX_archived_items_envelope_id",
            timestampIndexName: "IX_archived_items_timestamp",
            levelIndexName: "IX_archived_items_level"
        );
    } // End of Method OnModelCreating

    private static void ConfigureEnvelopeSet<TEnvelope, TItem>(
        EntityTypeBuilder<TEnvelope> builder,
        string tableName,
        string receivedIndexName,
        string serviceIndexName,
        string environmentIndexName,
        string acknowledgedIndexName,
        string deletedIndexName
    ) where TEnvelope : TelemetryEnvelopeRecordBase<TItem>
        where TItem : TelemetryItemRecordBase
    {
        builder.ToTable(tableName);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Host).IsRequired();
        builder.Property(e => e.Environment).IsRequired();
        builder.Property(e => e.Service).IsRequired();
        builder.Property(e => e.SeverityThreshold).IsRequired();
        builder.Property(e => e.WindowMinutes).IsRequired();
        builder.Property(e => e.MaxItems).IsRequired();
        builder.Property(e => e.ItemCount).IsRequired();
        builder.Property(e => e.ReceivedAtUtc).IsRequired();
        builder.Property(e => e.AcknowledgedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.DeletedAtUtc).HasColumnType("TEXT");

        builder.HasIndex(e => e.ReceivedAtUtc).HasDatabaseName(receivedIndexName);
        builder.HasIndex(e => e.Service).HasDatabaseName(serviceIndexName);
        builder.HasIndex(e => e.Environment).HasDatabaseName(environmentIndexName);
        builder.HasIndex(e => e.AcknowledgedAtUtc).HasDatabaseName(acknowledgedIndexName);
        builder.HasIndex(e => e.DeletedAtUtc).HasDatabaseName(deletedIndexName);
    } // End of Method ConfigureEnvelopeSet

    private static void ConfigureItemSet<TItem, TEnvelope>(
        EntityTypeBuilder<TItem> builder,
        string tableName,
        string envelopeIdIndexName,
        string timestampIndexName,
        string levelIndexName
    ) where TItem : TelemetryItemRecordBase
        where TEnvelope : TelemetryEnvelopeRecordBase<TItem>
    {
        builder.ToTable(tableName);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.TimestampUtc).IsRequired();
        builder.Property(e => e.Level).IsRequired();
        builder.Property(e => e.Message).IsRequired();
        builder.Property(e => e.TemplateHash).IsRequired();
        builder.Property(e => e.Count).IsRequired();
        builder.Property(e => e.PropertiesJson).HasColumnType("TEXT");

        builder.HasIndex(e => e.EnvelopeId).HasDatabaseName(envelopeIdIndexName);
        builder.HasIndex(e => e.TimestampUtc).HasDatabaseName(timestampIndexName);
        builder.HasIndex(e => e.Level).HasDatabaseName(levelIndexName);

        builder.HasOne<TEnvelope>("Envelope")
            .WithMany("Items")
            .HasForeignKey(e => e.EnvelopeId)
            .OnDelete(DeleteBehavior.Cascade);
    } // End of Method ConfigureItemSet
} // End of Class TelemetryDbContext
