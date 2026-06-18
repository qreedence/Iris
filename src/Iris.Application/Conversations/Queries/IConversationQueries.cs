namespace Iris.Application.Conversations.Queries
{
    public interface IConversationQueries
    {
        Task<IReadOnlyList<ConversationSummaryDto>> GetAllAsync(Guid userId, int skip = 0, int take = 50, CancellationToken ct = default);
        Task<IReadOnlyList<ConversationMessageDto>?> GetMessagesAsync(Guid userId, Guid conversationId, int skip = 0, int take = 100, CancellationToken ct = default);
        Task<bool> ExistsForUserAsync(Guid userId, Guid conversationId, CancellationToken ct = default);
    }
}
