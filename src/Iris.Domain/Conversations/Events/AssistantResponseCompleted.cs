using Iris.Domain.Conversations.Content;

namespace Iris.Domain.Conversations.Events
{
    public record AssistantResponseCompleted(
        Guid Id,
        Guid ConversationId,
        IReadOnlyList<MessageContentBlock> ContentBlocks,
        string Model
    ) : ConversationEvent(ConversationId);
}