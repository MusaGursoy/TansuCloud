// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TansuCloud.Telemetry.Data;

#nullable disable

namespace TansuCloud.Telemetry.Migrations;

/// <summary>
/// Initial telemetry schema containing envelopes and items tables.
/// </summary>
[DbContext(typeof(TelemetryDbContext))]
[Migration("20251003160000_InitialCreate")]
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "telemetry_envelopes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Host = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Environment = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Service = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                SeverityThreshold = table.Column<string>(
                    type: "TEXT",
                    maxLength: 32,
                    nullable: false
                ),
                WindowMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                MaxItems = table.Column<int>(type: "INTEGER", nullable: false),
                ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_telemetry_envelopes", x => x.Id);
            }
        );

        migrationBuilder.CreateTable(
            name: "telemetry_items",
            columns: table => new
            {
                Id = table
                    .Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EnvelopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                TemplateHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Exception = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                Service = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Environment = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                TenantHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                TraceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                SpanId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                Category = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                EventId = table.Column<int>(type: "INTEGER", nullable: true),
                Count = table.Column<int>(type: "INTEGER", nullable: false),
                PropertiesJson = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_telemetry_items", x => x.Id);
                table.ForeignKey(
                    name: "FK_telemetry_items_telemetry_envelopes_EnvelopeId",
                    column: x => x.EnvelopeId,
                    principalTable: "telemetry_envelopes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_envelopes_acknowledged_at",
            table: "telemetry_envelopes",
            column: "AcknowledgedAtUtc"
        );

        migrationBuilder.CreateIndex(
            name: "IX_envelopes_deleted_at",
            table: "telemetry_envelopes",
            column: "DeletedAtUtc"
        );

        migrationBuilder.CreateIndex(
            name: "IX_envelopes_environment",
            table: "telemetry_envelopes",
            column: "Environment"
        );

        migrationBuilder.CreateIndex(
            name: "IX_envelopes_received_at",
            table: "telemetry_envelopes",
            column: "ReceivedAtUtc"
        );

        migrationBuilder.CreateIndex(
            name: "IX_envelopes_service",
            table: "telemetry_envelopes",
            column: "Service"
        );

        migrationBuilder.CreateIndex(
            name: "IX_items_envelope_id",
            table: "telemetry_items",
            column: "EnvelopeId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_items_level",
            table: "telemetry_items",
            column: "Level"
        );

        migrationBuilder.CreateIndex(
            name: "IX_items_timestamp",
            table: "telemetry_items",
            column: "TimestampUtc"
        );
    } // End of Method Up

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "telemetry_items");

        migrationBuilder.DropTable(name: "telemetry_envelopes");
    } // End of Method Down
} // End of Class InitialCreate
