namespace Iris.Application.AiIntegration.Models;

public record ToolOptions(
    IReadOnlyList<ToolDefinition> Tools,
    ToolChoice? ToolChoice = null);
