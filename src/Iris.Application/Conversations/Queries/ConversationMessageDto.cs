using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;

namespace Iris.Application.Conversations.Queries;

public record ConversationMessageDto(
    Guid Id,
    Guid ConversationId,
    ChatRole Role,
    IReadOnlyList<MessageContentBlock> ContentBlocks,
    DateTimeOffset CreatedAt);