using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;

namespace Iris.Domain.Conversations.Events
{
    public record AssistantResponseCompleted(
        Guid Id,
        Guid ConversationId,
        Guid MessageId,
        IReadOnlyList<MessageContentBlock> ContentBlocks,
        string Model,
        FinishReason FinishReason,
        int InputTokens = 0,
        int OutputTokens = 0
    ) : ConversationEvent(ConversationId);
}
