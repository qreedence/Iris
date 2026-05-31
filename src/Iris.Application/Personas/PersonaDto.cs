namespace Iris.Application.Personas;

public record PersonaDto(
    Guid Id,
    string Name,
    string? SystemPrompt,
    string? ModelPreference,
    string? Role,
    string? Group,
    string? Avatar,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
