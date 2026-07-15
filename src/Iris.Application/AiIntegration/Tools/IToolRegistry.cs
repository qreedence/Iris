using Iris.Application.AiIntegration.Models;

namespace Iris.Application.AiIntegration.Tools;

public interface IToolRegistry
{
    Task<IReadOnlyList<ToolDefinition>> GetToolsForPersonaAsync(
        Guid personaId,
        CancellationToken ct = default);

    Task<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolContext context,
        CancellationToken ct = default);
}
