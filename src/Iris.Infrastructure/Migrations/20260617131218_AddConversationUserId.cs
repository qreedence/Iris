using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "conversation_read_models",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_conversation_read_models_UserId",
                table: "conversation_read_models",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversation_read_models_UserId",
                table: "conversation_read_models");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "conversation_read_models");
        }
    }
}
