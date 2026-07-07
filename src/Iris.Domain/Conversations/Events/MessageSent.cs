using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;

namespace Iris.Domain.Conversations.Events
{
    public record MessageSent(
        Guid Id,
        Guid ConversationId,
        IReadOnlyList<MessageContentBlock> ContentBlocks,
        ChatRole Role
    ) : ConversationEvent(ConversationId);
}