namespace Iris.Domain.Conversations.Events
{
    public record TurnCompleted(
        Guid ConversationId,
        Guid MessageId,
        int InputTokens, 
        int OutputTokens,
        int LastRoundInputTokens = 0
    ) : ConversationEvent(ConversationId);
}
