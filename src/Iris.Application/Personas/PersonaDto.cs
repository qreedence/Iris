namespace Iris.Application.Personas;

public record PersonaDto(
    Guid Id,
    string Name,
    string? SystemPrompt,
    string? ModelPreference,
    string? Avatar,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
