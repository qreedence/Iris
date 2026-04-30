namespace Iris.Domain.Conversations.Events
{
    public record ConversationArchived(Guid ConversationId) : ConversationEvent(ConversationId);
}
