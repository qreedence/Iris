namespace Iris.Domain.Conversations.Events;

public record ModelChanged(
    Guid ConversationId,
    string Model
) : ConversationEvent(ConversationId);
