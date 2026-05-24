namespace Iris.Application.Personas;

public record CreatePersonaRequest(
    Guid UserId,
    string Name,
    string? SystemPrompt = null,
    string? ModelPreference = null,
    string? Role = null,
    string? Group = null,
    string? Avatar = null);
