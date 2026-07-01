using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Application.Personas;

public record SystemPromptSectionsRequest(
    string? Identity = null,
    string? Voice = null,
    string? Role = null,
    string? Relationship = null,
    string? ToolInstructions = null)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
