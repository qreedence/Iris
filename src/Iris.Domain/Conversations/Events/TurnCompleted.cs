namespace Iris.Domain.Conversations.Events
{
    public record TurnCompleted(
        Guid ConversationId,
        int InputTokens, 
        int OutputTokens
    ) : ConversationEvent(ConversationId);
}
