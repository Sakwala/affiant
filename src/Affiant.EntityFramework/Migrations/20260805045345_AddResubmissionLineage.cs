using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiant.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddResubmissionLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ResubmittedTo",
                schema: "affiant",
                table: "Docket",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Docket_ResubmittedTo",
                schema: "affiant",
                table: "Docket",
                column: "ResubmittedTo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Docket_ResubmittedTo",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "ResubmittedTo",
                schema: "affiant",
                table: "Docket");
        }
    }
}
