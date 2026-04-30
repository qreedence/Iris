namespace Iris.Domain.Conversations.Events
{
    public record ConversationCreated(
        Guid ConversationId,
        Guid PersonaId, 
        string Title
    ) : ConversationEvent(ConversationId);
}
