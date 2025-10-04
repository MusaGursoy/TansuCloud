// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace TansuCloud.Telemetry.Data.Migrations;

/// <summary>
/// Model snapshot for the telemetry ingestion service.
/// </summary>
[DbContext(typeof(TelemetryDbContext))]
public class TelemetryDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "9.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Entities.TelemetryEnvelopeEntity",
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

                b.HasIndex("AcknowledgedAtUtc").HasDatabaseName("IX_envelopes_acknowledged_at");

                b.HasIndex("DeletedAtUtc").HasDatabaseName("IX_envelopes_deleted_at");

                b.HasIndex("Environment").HasDatabaseName("IX_envelopes_environment");

                b.HasIndex("ReceivedAtUtc").HasDatabaseName("IX_envelopes_received_at");

                b.HasIndex("Service").HasDatabaseName("IX_envelopes_service");

                b.ToTable("telemetry_envelopes", (string)null);
            }
        );

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Entities.TelemetryItemEntity",
            b =>
            {
                b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");

                b.Property<int>("Count").HasColumnType("INTEGER");

                b.Property<string>("Category").HasMaxLength(128).HasColumnType("TEXT");

                b.Property<string>("CorrelationId").HasMaxLength(64).HasColumnType("TEXT");

                b.Property<string>("Environment").HasMaxLength(64).HasColumnType("TEXT");

                b.Property<Guid>("EnvelopeId").HasColumnType("TEXT");

                b.Property<int?>("EventId").HasColumnType("INTEGER");

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

                b.HasIndex("EnvelopeId").HasDatabaseName("IX_items_envelope_id");

                b.HasIndex("Level").HasDatabaseName("IX_items_level");

                b.HasIndex("TimestampUtc").HasDatabaseName("IX_items_timestamp");

                b.ToTable("telemetry_items", (string)null);

                b.HasOne("TansuCloud.Telemetry.Data.Entities.TelemetryEnvelopeEntity", "Envelope")
                    .WithMany("Items")
                    .HasForeignKey("EnvelopeId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();

                b.Navigation("Envelope");
            }
        );

        modelBuilder.Entity(
            "TansuCloud.Telemetry.Data.Entities.TelemetryEnvelopeEntity",
            b =>
            {
                b.Navigation("Items");
            }
        );
#pragma warning restore 612, 618
    }
} // End of Class TelemetryDbContextModelSnapshot
