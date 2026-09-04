using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Affiant.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddDocketRowFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AmendedAffidavitJson",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmendedProvenanceChainsJson",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttestationJson",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockedJson",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompositeRef",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CreatedAtTicks",
                schema: "affiant",
                table: "Docket",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecidedAt",
                schema: "affiant",
                table: "Docket",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DecidedAtTicks",
                schema: "affiant",
                table: "Docket",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionJson",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Execution",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionDetail",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExpiresAtTicks",
                schema: "affiant",
                table: "Docket",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PreservedAmendmentsJson",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtocolVersion",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: false,
                defaultValue: "0.1.0");

            migrationBuilder.AddColumn<Guid>(
                name: "Supersedes",
                schema: "affiant",
                table: "Docket",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolName",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Requirement",
                schema: "affiant",
                table: "Docket",
                type: "text",
                nullable: true);

            // Give every pre-existing row the tick values its instants imply. The tick columns are
            // what every bounded read compares and orders by — the deadline, the filing order, the
            // retention cut-off — and a row left at the column default of zero would read as filed
            // and due at the beginning of time: expired the moment the sweep ran, and eligible for
            // retention immediately. 621355968000000000 is the .NET tick value of the Unix epoch.
            migrationBuilder.Sql(
                """
                UPDATE affiant."Docket"
                SET "CreatedAtTicks" = (EXTRACT(EPOCH FROM "CreatedAt") * 10000000)::bigint
                                       + 621355968000000000,
                    "ExpiresAtTicks" = (EXTRACT(EPOCH FROM "ExpiresAt") * 10000000)::bigint
                                       + 621355968000000000
                WHERE "CreatedAtTicks" = 0 OR "ExpiresAtTicks" = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Docket_Status_DecidedAtTicks",
                schema: "affiant",
                table: "Docket",
                columns: new[] { "Status", "DecidedAtTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_Docket_Status_ExpiresAtTicks",
                schema: "affiant",
                table: "Docket",
                columns: new[] { "Status", "ExpiresAtTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_Docket_Supersedes",
                schema: "affiant",
                table: "Docket",
                column: "Supersedes");

            migrationBuilder.CreateIndex(
                name: "IX_Docket_TenantId_Status_CreatedAtTicks",
                schema: "affiant",
                table: "Docket",
                columns: new[] { "TenantId", "Status", "CreatedAtTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Docket_Status_DecidedAtTicks",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropIndex(
                name: "IX_Docket_Status_ExpiresAtTicks",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropIndex(
                name: "IX_Docket_Supersedes",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropIndex(
                name: "IX_Docket_TenantId_Status_CreatedAtTicks",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "AmendedAffidavitJson",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "AmendedProvenanceChainsJson",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "AttestationJson",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "BlockedJson",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "CompositeRef",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "CreatedAtTicks",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "DecidedAt",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "DecidedAtTicks",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "DecisionJson",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "Execution",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "ExecutionDetail",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "ExpiresAtTicks",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "PreservedAmendmentsJson",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "ProtocolVersion",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "Supersedes",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "ToolName",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "Channel",
                schema: "affiant",
                table: "Docket");

            migrationBuilder.DropColumn(
                name: "Requirement",
                schema: "affiant",
                table: "Docket");
        }
    }
}
