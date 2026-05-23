namespace Iris.Domain.Conversations.Events
{
    public record TurnCancelled(
        Guid ConversationId,
        string? PartialContent
    ) : ConversationEvent(ConversationId);
}
