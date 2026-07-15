using Iris.Domain.AiIntegration;

namespace Iris.Application.AiIntegration.Tools;

public record ToolResult(
    string PayloadJson,
    string? Preview,
    ToolExecutionStatus Status);
