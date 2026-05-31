namespace Iris.Application.Conversations.Queries;

public record ConversationSummaryDto(
    Guid Id,
    Guid PersonaId,
    string Title,
    string? CurrentModel,
    DateTimeOffset CreatedAt,
    int MessageCount,
    DateTimeOffset? LastMessageAt);
