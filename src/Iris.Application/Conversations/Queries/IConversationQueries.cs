namespace Iris.Application.Conversations.Queries
{
    public interface IConversationQueries
    {
        Task<IReadOnlyList<ConversationSummaryDto>> GetAllAsync(int skip = 0, int take = 50, CancellationToken ct = default);
        Task<IReadOnlyList<ConversationMessageDto>?> GetMessagesAsync(Guid conversationId, int skip = 0, int take = 100, CancellationToken ct = default);
    }
}
