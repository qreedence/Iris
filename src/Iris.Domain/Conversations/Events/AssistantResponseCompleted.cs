namespace Iris.Domain.Conversations.Events
{
    public record AssistantResponseCompleted(
        Guid ConversationId,
        string Content,
        string Model
    ) : ConversationEvent(ConversationId);
}