using Iris.Domain.AiIntegration;

namespace Iris.Application.Conversations.Queries;

public record ConversationMessageDto(
    Guid Id,
    Guid ConversationId,
    ChatRole Role,
    string Content,
    DateTimeOffset CreatedAt);
