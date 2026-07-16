using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrchestratorPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "personas",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "User");

            migrationBuilder.CreateTable(
                name: "persona_creation_tool_executions",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolCallId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PersonaId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_persona_creation_tool_executions", x => new { x.ConversationId, x.ToolCallId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_personas_UserId_System",
                table: "personas",
                column: "UserId",
                unique: true,
                filter: "\"Kind\" = 'System' AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "persona_creation_tool_executions");

            migrationBuilder.DropIndex(
                name: "IX_personas_UserId_System",
                table: "personas");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "personas");
        }
    }
}
