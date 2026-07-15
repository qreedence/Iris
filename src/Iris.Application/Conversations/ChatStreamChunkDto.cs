using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;

namespace Iris.Application.Conversations;

public record ChatStreamChunkDto(
    Guid ConversationId,
    Guid MessageId,
    ContentBlockType BlockType,
    int BlockIndex,
    string? Content,
    string? ToolCallId = null,
    string? Name = null,
    string? ArgumentsJson = null,
    Guid? PayloadId = null,
    ToolExecutionStatus? Status = null,
    long? DurationMs = null);
