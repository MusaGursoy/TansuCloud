// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TansuCloud.Telemetry.Data;

#nullable disable

namespace TansuCloud.Telemetry.Migrations;

/// <summary>
/// Snapshot of the telemetry database model used by Entity Framework Core.
/// </summary>
[DbContext(typeof(TelemetryDbContext))]
public class TelemetryDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "9.0.9");

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Records.TelemetryActiveEnvelopeRecord",
            b =>
            {
                b.Property<Guid>("Id").HasColumnType("TEXT");

                b.Property<DateTime?>("AcknowledgedAtUtc").HasColumnType("TEXT");

                b.Property<DateTime?>("DeletedAtUtc").HasColumnType("TEXT");

                b.Property<string>("Environment")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("TEXT");

                b.Property<string>("Host").IsRequired().HasMaxLength(200).HasColumnType("TEXT");

                b.Property<int>("ItemCount").HasColumnType("INTEGER");

                b.Property<int>("MaxItems").HasColumnType("INTEGER");

                b.Property<DateTime>("ReceivedAtUtc").HasColumnType("TEXT");

                b.Property<string>("Service").IsRequired().HasMaxLength(100).HasColumnType("TEXT");

                b.Property<string>("SeverityThreshold")
                    .IsRequired()
                    .HasMaxLength(32)
                    .HasColumnType("TEXT");

                b.Property<int>("WindowMinutes").HasColumnType("INTEGER");

                b.HasKey("Id");

                b.HasIndex("AcknowledgedAtUtc").HasDatabaseName("IX_active_envelopes_acknowledged_at");

                b.HasIndex("DeletedAtUtc").HasDatabaseName("IX_active_envelopes_deleted_at");

                b.HasIndex("Environment").HasDatabaseName("IX_active_envelopes_environment");

                b.HasIndex("ReceivedAtUtc").HasDatabaseName("IX_active_envelopes_received_at");

                b.HasIndex("Service").HasDatabaseName("IX_active_envelopes_service");

                b.ToTable("telemetry_envelopes_active");
            }
        );

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Records.TelemetryArchivedEnvelopeRecord",
            b =>
            {
                b.Property<Guid>("Id").HasColumnType("TEXT");

                b.Property<DateTime?>("AcknowledgedAtUtc").HasColumnType("TEXT");

                b.Property<DateTime?>("DeletedAtUtc").HasColumnType("TEXT");

                b.Property<string>("Environment")
                    .IsRequired()
                    .HasMaxLength(64)
                    .HasColumnType("TEXT");

                b.Property<string>("Host").IsRequired().HasMaxLength(200).HasColumnType("TEXT");

                b.Property<int>("ItemCount").HasColumnType("INTEGER");

                b.Property<int>("MaxItems").HasColumnType("INTEGER");

                b.Property<DateTime>("ReceivedAtUtc").HasColumnType("TEXT");

                b.Property<string>("Service").IsRequired().HasMaxLength(100).HasColumnType("TEXT");

                b.Property<string>("SeverityThreshold")
                    .IsRequired()
                    .HasMaxLength(32)
                    .HasColumnType("TEXT");

                b.Property<int>("WindowMinutes").HasColumnType("INTEGER");

                b.HasKey("Id");

                b.HasIndex("AcknowledgedAtUtc").HasDatabaseName("IX_archived_envelopes_acknowledged_at");

                b.HasIndex("DeletedAtUtc").HasDatabaseName("IX_archived_envelopes_deleted_at");

                b.HasIndex("Environment").HasDatabaseName("IX_archived_envelopes_environment");

                b.HasIndex("ReceivedAtUtc").HasDatabaseName("IX_archived_envelopes_received_at");

                b.HasIndex("Service").HasDatabaseName("IX_archived_envelopes_service");

                b.ToTable("telemetry_envelopes_archive");
            }
        );

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Records.TelemetryActiveItemRecord",
            b =>
            {
                b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");

                b.Property<string>("Category").HasMaxLength(128).HasColumnType("TEXT");

                b.Property<string>("CorrelationId").HasMaxLength(64).HasColumnType("TEXT");

                b.Property<int>("Count").HasColumnType("INTEGER");

                b.Property<string>("Environment").HasMaxLength(64).HasColumnType("TEXT");

                b.Property<int?>("EventId").HasColumnType("INTEGER");

                b.Property<Guid>("EnvelopeId").HasColumnType("TEXT");

                b.Property<string>("Exception").HasMaxLength(2048).HasColumnType("TEXT");

                b.Property<string>("Kind").IsRequired().HasMaxLength(32).HasColumnType("TEXT");

                b.Property<string>("Level").IsRequired().HasMaxLength(32).HasColumnType("TEXT");

                b.Property<string>("Message").IsRequired().HasMaxLength(1024).HasColumnType("TEXT");

                b.Property<string>("PropertiesJson").HasColumnType("TEXT");

                b.Property<string>("Service").HasMaxLength(100).HasColumnType("TEXT");

                b.Property<string>("SpanId").HasMaxLength(32).HasColumnType("TEXT");

                b.Property<string>("TemplateHash")
                    .IsRequired()
                    .HasMaxLength(128)
                    .HasColumnType("TEXT");

                b.Property<string>("TenantHash").HasMaxLength(128).HasColumnType("TEXT");

                b.Property<DateTime>("TimestampUtc").HasColumnType("TEXT");

                b.Property<string>("TraceId").HasMaxLength(64).HasColumnType("TEXT");

                b.HasKey("Id");

                b.HasIndex("EnvelopeId").HasDatabaseName("IX_active_items_envelope_id");

                b.HasIndex("Level").HasDatabaseName("IX_active_items_level");

                b.HasIndex("TimestampUtc").HasDatabaseName("IX_active_items_timestamp");

                b.ToTable("telemetry_items_active");

                b.HasOne("TansuCloud.Telemetry.Data.Records.TelemetryActiveEnvelopeRecord", "Envelope")
                    .WithMany("Items")
                    .HasForeignKey("EnvelopeId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();

                b.Navigation("Envelope");
            }
        );

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Records.TelemetryArchivedItemRecord",
            b =>
            {
                b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");

                b.Property<string>("Category").HasMaxLength(128).HasColumnType("TEXT");

                b.Property<string>("CorrelationId").HasMaxLength(64).HasColumnType("TEXT");

                b.Property<int>("Count").HasColumnType("INTEGER");

                b.Property<string>("Environment").HasMaxLength(64).HasColumnType("TEXT");

                b.Property<int?>("EventId").HasColumnType("INTEGER");

                b.Property<Guid>("EnvelopeId").HasColumnType("TEXT");

                b.Property<string>("Exception").HasMaxLength(2048).HasColumnType("TEXT");

                b.Property<string>("Kind").IsRequired().HasMaxLength(32).HasColumnType("TEXT");

                b.Property<string>("Level").IsRequired().HasMaxLength(32).HasColumnType("TEXT");

                b.Property<string>("Message").IsRequired().HasMaxLength(1024).HasColumnType("TEXT");

                b.Property<string>("PropertiesJson").HasColumnType("TEXT");

                b.Property<string>("Service").HasMaxLength(100).HasColumnType("TEXT");

                b.Property<string>("SpanId").HasMaxLength(32).HasColumnType("TEXT");

                b.Property<string>("TemplateHash")
                    .IsRequired()
                    .HasMaxLength(128)
                    .HasColumnType("TEXT");

                b.Property<string>("TenantHash").HasMaxLength(128).HasColumnType("TEXT");

                b.Property<DateTime>("TimestampUtc").HasColumnType("TEXT");

                b.Property<string>("TraceId").HasMaxLength(64).HasColumnType("TEXT");

                b.HasKey("Id");

                b.HasIndex("EnvelopeId").HasDatabaseName("IX_archived_items_envelope_id");

                b.HasIndex("Level").HasDatabaseName("IX_archived_items_level");

                b.HasIndex("TimestampUtc").HasDatabaseName("IX_archived_items_timestamp");

                b.ToTable("telemetry_items_archive");

                b.HasOne("TansuCloud.Telemetry.Data.Records.TelemetryArchivedEnvelopeRecord", "Envelope")
                    .WithMany("Items")
                    .HasForeignKey("EnvelopeId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();

                b.Navigation("Envelope");
            }
        );

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Records.TelemetryActiveEnvelopeRecord",
            b =>
            {
                b.Navigation("Items");
            }
        );

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Records.TelemetryArchivedEnvelopeRecord",
            b =>
            {
                b.Navigation("Items");
            }
        );
#pragma warning restore 612, 618
    } // End of Method BuildModel
} // End of Class TelemetryDbContextModelSnapshot
