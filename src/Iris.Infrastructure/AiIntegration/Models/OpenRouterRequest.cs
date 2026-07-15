using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Infrastructure.AiIntegration.Models
{
    internal record OpenRouterRequest(
        string Model,
        IReadOnlyList<object> Input,
        string? Instructions = null,
        float? Temperature = null,
        int? MaxOutputTokens = null,
        float? TopP = null,
        bool? Stream = null,
        IReadOnlyList<OpenRouterTool>? Tools = null,
        object? ToolChoice = null
    );

    internal record OpenRouterMessage(
        string Role,
        string Content,
        [property: JsonPropertyName("reasoning_details")] IReadOnlyList<IReadOnlyDictionary<string, object?>>? ReasoningDetails = null,
        string? Reasoning = null
    );

    internal record OpenRouterTool(
        string Type,
        string Name,
        string Description,
        JsonElement Parameters);

    internal record OpenRouterForcedToolChoice(string Type, string Name);

    internal record OpenRouterFunctionCall(
        string Type,
        string Id,
        [property: JsonPropertyName("call_id")] string CallId,
        string Name,
        string Arguments);

    internal record OpenRouterFunctionCallOutput(
        string Type,
        [property: JsonPropertyName("call_id")] string CallId,
        string Output);
}
