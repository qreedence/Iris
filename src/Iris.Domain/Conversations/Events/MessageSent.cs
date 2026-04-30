using Iris.Domain.AiIntegration;

namespace Iris.Domain.Conversations.Events
{
    public record MessageSent(
        Guid ConversationId,
        string Content,
        ChatRole Role
    ) : ConversationEvent(ConversationId);
}
