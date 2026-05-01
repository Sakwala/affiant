using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiant.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddDocketEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Docket",
                schema: "affiant",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ReviewerUserId = table.Column<string>(type: "text", nullable: true),
                    OperationType = table.Column<string>(type: "text", nullable: false),
                    Affidavit = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    ProvenanceChains = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    Amendments = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false, defaultValue: "Pending")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Docket", x => x.EntryId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Docket_SessionId_Status",
                schema: "affiant",
                table: "Docket",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Docket_TenantId_Status",
                schema: "affiant",
                table: "Docket",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Docket",
                schema: "affiant");
        }
    }
}
