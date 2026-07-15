namespace Iris.Application.AiIntegration.Models;

public record ToolCall(
    string Id,
    string FunctionName,
    string ArgumentsJson,
    string? ProviderItemId = null);
