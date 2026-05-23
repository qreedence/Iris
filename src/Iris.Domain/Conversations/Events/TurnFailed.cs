namespace Iris.Domain.Conversations.Events
{
    public record TurnFailed(
        Guid ConversationId,
        FailureSource Source,
        string ErrorCode,
        string Message,
        string? PartialContent
    ) : ConversationEvent(ConversationId);
}
