namespace Iris.Domain.Conversations.Events
{
    public record TurnCancelled(
        Guid ConversationId,
        string? PartialContent,
        // Id of the MessageSent event this cancellation terminates. Optional for
        // back-compat: events stored before this field existed have no messageId
        // property in their JSON and deserialize to null. Non-null values let retry
        // idempotency stay exact (a cancelled LATER turn must not make an earlier
        // crashed turn look terminal).
        Guid? MessageId = null
    ) : ConversationEvent(ConversationId);
}
