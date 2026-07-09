namespace Iris.Domain.Conversations.Content;

public class ToolResultPayload
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string ToolCallId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string? Preview { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
