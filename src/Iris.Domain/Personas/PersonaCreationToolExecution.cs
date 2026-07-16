namespace Iris.Domain.Personas;

public class PersonaCreationToolExecution
{
    public Guid ConversationId { get; set; }
    public string ToolCallId { get; set; } = string.Empty;
    public Guid PersonaId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
