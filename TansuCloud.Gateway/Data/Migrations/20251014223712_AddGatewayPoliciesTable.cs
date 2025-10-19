using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TansuCloud.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGatewayPoliciesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_policies",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_policies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_policies_enabled",
                table: "gateway_policies",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_policies_type",
                table: "gateway_policies",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_policies_type_enabled",
                table: "gateway_policies",
                columns: new[] { "type", "enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_policies");
        }
    }
}
