using Iris.Domain.AiIntegration;

namespace Iris.Domain.Conversations.Events
{
    public record MessageSent(
        Guid Id,
        Guid ConversationId,
        string Content,
        ChatRole Role
    ) : ConversationEvent(ConversationId);
}
