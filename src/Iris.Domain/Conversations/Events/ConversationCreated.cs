namespace Iris.Domain.Conversations.Events
{
    public record ConversationCreated(
        Guid ConversationId,
        Guid UserId,
        Guid PersonaId,
        string Title
    ) : ConversationEvent(ConversationId);
}
