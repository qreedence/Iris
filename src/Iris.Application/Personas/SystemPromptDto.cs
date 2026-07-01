namespace Iris.Application.Personas;

public record SystemPromptDto(
    string? Identity,
    string? Voice,
    string? Role,
    string? Relationship,
    string? ToolInstructions)
{
    public static SystemPromptDto Empty { get; } = new(null, null, null, null, null);
}
