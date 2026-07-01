namespace Iris.Application.Personas;

public record CreatePersonaRequest(
    string Name,
    SystemPromptSectionsRequest? SystemPrompt = null,
    string? ModelPreference = null,
    string? Role = null,
    string? Group = null,
    string? Avatar = null);
