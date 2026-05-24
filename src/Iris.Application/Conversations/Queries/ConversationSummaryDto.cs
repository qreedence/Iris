namespace Iris.Application.Conversations.Queries;

public record ConversationSummaryDto(
    Guid Id,
    Guid PersonaId,
    string Title,
    DateTimeOffset CreatedAt,
    int MessageCount,
    DateTimeOffset? LastMessageAt);
