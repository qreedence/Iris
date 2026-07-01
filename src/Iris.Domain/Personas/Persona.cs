namespace Iris.Domain.Personas;

public class Persona
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SystemPrompt SystemPrompt { get; set; } = null!;
    public string? ModelPreference { get; set; }
    public string? Role { get; set; }
    public string? Group { get; set; }
    public string? Avatar { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
