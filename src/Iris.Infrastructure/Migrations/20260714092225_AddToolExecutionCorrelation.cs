using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iris.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddToolExecutionCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tool_result_payloads_ConversationId",
                table: "tool_result_payloads");

            migrationBuilder.DropIndex(
                name: "IX_tool_result_payloads_ToolCallId",
                table: "tool_result_payloads");

            migrationBuilder.CreateIndex(
                name: "IX_tool_result_payloads_ConversationId_ToolCallId",
                table: "tool_result_payloads",
                columns: new[] { "ConversationId", "ToolCallId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tool_result_payloads_ConversationId_ToolCallId",
                table: "tool_result_payloads");

            migrationBuilder.CreateIndex(
                name: "IX_tool_result_payloads_ConversationId",
                table: "tool_result_payloads",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_result_payloads_ToolCallId",
                table: "tool_result_payloads",
                column: "ToolCallId");
        }
    }
}
