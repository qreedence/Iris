namespace Iris.Domain.Conversations.Events
{
    public record TurnFailed(
        Guid ConversationId,
        Guid MessageId,
        FailureSource Source,
        string ErrorCode,
        string Message,
        string? PartialContent
    ) : ConversationEvent(ConversationId);
}
