namespace Iris.Domain.Personas;

public class SystemPrompt
{
    public Guid PersonaId { get; set; }
    public Persona Persona { get; set; } = null!;
    public string? Identity { get; set; }
    public string? Voice { get; set; }
    public string? Role { get; set; }
    public string? Relationship { get; set; }
    public string? ToolInstructions { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
