using Iris.Application.Conversations.Queries;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Persistence
{
    public class ConversationQueries : IConversationQueries
    {
        private readonly AppDbContext _db;

        public ConversationQueries(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<ConversationSummaryDto>> GetAllAsync(int skip = 0, int take = 50, CancellationToken ct = default)
        {
            var conversations = await _db.ConversationReadModels
           .AsNoTracking()
           .OrderByDescending(c => c.CreatedAt)
           .Skip(skip)
           .Take(take)
           .Select(c => new ConversationSummaryDto(
               c.Id,
               c.Title,
               c.CreatedAt,
               c.MessageCount,
               c.LastMessageAt))
           .ToListAsync(ct);

            return conversations;
        }

        public async Task<List<ConversationMessageDto>?> GetMessagesAsync(Guid conversationId, int skip = 0, int take = 100, CancellationToken ct = default)
        {
            var exists = await _db.ConversationReadModels
            .AsNoTracking()
            .AnyAsync(c => c.Id == conversationId, ct);

            if (!exists)
                return null;

            var messages = await _db.ConversationMessages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(m => new ConversationMessageDto(
                    m.Id,
                    m.ConversationId,
                    m.Role,
                    m.Content,
                    m.CreatedAt))
                .ToListAsync(ct);

            return messages;
        }
    }
}
