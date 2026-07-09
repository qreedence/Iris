using System.Text.Json.Serialization;

namespace Iris.Infrastructure.AiIntegration.Models
{
    internal record OpenRouterRequest(
        string Model,
        List<OpenRouterMessage> Input,
        string? Instructions = null,
        float? Temperature = null,
        int? MaxOutputTokens = null,
        float? TopP = null,
        bool? Stream = null
    );

    internal record OpenRouterMessage(
        string Role,
        string Content,
        [property: JsonPropertyName("reasoning_details")] IReadOnlyList<IReadOnlyDictionary<string, object?>>? ReasoningDetails = null,
        string? Reasoning = null
    );
}