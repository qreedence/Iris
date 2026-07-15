using Iris.Domain.AiIntegration;

namespace Iris.Domain.Conversations.Events;

public record ToolExecuted(
    Guid ConversationId,
    Guid MessageId,
    string ToolCallId,
    string Name,
    Guid PayloadId,
    ToolExecutionStatus Status,
    long DurationMs
) : ConversationEvent(ConversationId);
