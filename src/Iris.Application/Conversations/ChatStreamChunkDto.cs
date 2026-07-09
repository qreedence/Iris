using Iris.Domain.Conversations.Content;

namespace Iris.Application.Conversations;

public record ChatStreamChunkDto(
    Guid ConversationId,
    Guid MessageId,
    ContentBlockType BlockType,
    int BlockIndex,
    string Content);