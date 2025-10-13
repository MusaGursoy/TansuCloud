using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TansuCloud.Audit.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    when_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    span_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_action",
                table: "audit_events",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_category",
                table: "audit_events",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_correlation_id",
                table: "audit_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_metadata_gin",
                table: "audit_events",
                column: "metadata")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_service",
                table: "audit_events",
                column: "service");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_service_when",
                table: "audit_events",
                columns: new[] { "service", "when_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_subject",
                table: "audit_events",
                column: "subject");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id",
                table: "audit_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_when",
                table: "audit_events",
                columns: new[] { "tenant_id", "when_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_trace_id",
                table: "audit_events",
                column: "trace_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_when_utc",
                table: "audit_events",
                column: "when_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");
        }
    }
}
