using System.Text.Json;

namespace Iris.Application.AiIntegration.Models;

public record ToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema);
