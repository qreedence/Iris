namespace Iris.Application.Personas;

public record UpdatePersonaRequest(
    string Name,
    string? ModelPreference = null,
    string? Role = null,
    string? Group = null,
    string? Avatar = null);
