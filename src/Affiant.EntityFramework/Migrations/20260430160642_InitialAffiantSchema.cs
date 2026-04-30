using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Affiant.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class InitialAffiantSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "affiant");

            migrationBuilder.CreateTable(
                name: "ChatSessions",
                schema: "affiant",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                schema: "affiant",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    AuthorName = table.Column<string>(type: "text", nullable: true),
                    ModelId = table.Column<string>(type: "text", nullable: true),
                    ToolCallId = table.Column<string>(type: "text", nullable: true),
                    FunctionName = table.Column<string>(type: "text", nullable: true),
                    Arguments = table.Column<string>(type: "jsonb", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatSessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "affiant",
                        principalTable: "ChatSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationContexts",
                schema: "affiant",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    Entities = table.Column<string>(type: "jsonb", nullable: false),
                    FieldValues = table.Column<string>(type: "jsonb", nullable: false),
                    ProvenanceChains = table.Column<string>(type: "jsonb", nullable: false),
                    LastUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationContexts", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_ConversationContexts_ChatSessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "affiant",
                        principalTable: "ChatSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SessionId_Ordinal",
                schema: "affiant",
                table: "ChatMessages",
                columns: new[] { "SessionId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_TenantId_UserId",
                schema: "affiant",
                table: "ChatSessions",
                columns: new[] { "TenantId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages",
                schema: "affiant");

            migrationBuilder.DropTable(
                name: "ConversationContexts",
                schema: "affiant");

            migrationBuilder.DropTable(
                name: "ChatSessions",
                schema: "affiant");
        }
    }
}
