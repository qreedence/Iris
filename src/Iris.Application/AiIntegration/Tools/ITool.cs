using Iris.Application.AiIntegration.Models;

namespace Iris.Application.AiIntegration.Tools;

public interface ITool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> ExecuteAsync(
        string argumentsJson,
        ToolContext context,
        CancellationToken ct = default);
}
