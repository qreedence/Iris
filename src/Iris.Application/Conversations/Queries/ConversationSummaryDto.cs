namespace Iris.Application.Conversations.Queries;

public record ConversationSummaryDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    int MessageCount,
    DateTimeOffset? LastMessageAt);
