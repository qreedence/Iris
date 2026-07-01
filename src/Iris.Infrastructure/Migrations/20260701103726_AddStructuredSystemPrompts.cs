using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredSystemPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_prompts",
                columns: table => new
                {
                    PersonaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identity = table.Column<string>(type: "text", nullable: true),
                    Voice = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<string>(type: "text", nullable: true),
                    Relationship = table.Column<string>(type: "text", nullable: true),
                    ToolInstructions = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_prompts", x => x.PersonaId);
                    table.ForeignKey(
                        name: "FK_system_prompts_personas_PersonaId",
                        column: x => x.PersonaId,
                        principalTable: "personas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO system_prompts ("PersonaId", "Identity", "CreatedAt", "UpdatedAt")
                SELECT "Id", NULLIF(TRIM("SystemPrompt"), ''), "CreatedAt", "UpdatedAt"
                FROM personas
                """);

            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                table: "personas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                table: "personas",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE personas
                SET "SystemPrompt" = system_prompts."Identity"
                FROM system_prompts
                WHERE personas."Id" = system_prompts."PersonaId"
                """);

            migrationBuilder.DropTable(
                name: "system_prompts");
        }
    }
}
