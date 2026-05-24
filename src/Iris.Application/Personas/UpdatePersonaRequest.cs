namespace Iris.Application.Personas;

public record UpdatePersonaRequest(
    string Name,
    string? SystemPrompt = null,
    string? ModelPreference = null,
    string? Avatar = null);
