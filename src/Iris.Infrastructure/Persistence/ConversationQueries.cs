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

        public async Task<IReadOnlyList<ConversationSummaryDto>> GetAllAsync(int skip = 0, int take = 50, CancellationToken ct = default)
        {
            var conversations = await _db.ConversationReadModels
                .AsNoTracking()
                .OrderByDescending(c => c.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(c => new ConversationSummaryDto(
                    c.Id,
                    c.PersonaId,
                    c.Title,
                    c.CurrentModel,
                    c.CreatedAt,
                    c.MessageCount,
                    c.LastMessageAt))
                .ToListAsync(ct);

            return conversations;
        }

        public async Task<IReadOnlyList<ConversationMessageDto>?> GetMessagesAsync(Guid conversationId, int skip = 0, int take = 100, CancellationToken ct = default)
        {
            // LOAD-BEARING for tenant isolation: ConversationMessages has no EF query
            // filter, so this pre-check is the only thing stopping one user from
            // reading another user's messages below. Do not remove or reorder.
            var exists = await ExistsForUserAsync(conversationId, ct);

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
                    m.ContentBlocks,
                    m.CreatedAt))
                .ToListAsync(ct);

            return messages;
        }

        /// <summary>
        /// Scoping comes from the ConversationReadModel EF global query filter
        /// (keyed on ICurrentUserService, see AppDbContext.OnModelCreating) — the
        /// name describes semantics ("for the current user"), the filter provides
        /// the mechanism.
        /// </summary>
        public async Task<bool> ExistsForUserAsync(Guid conversationId, CancellationToken ct = default)
        {
            return await _db.ConversationReadModels
                .AsNoTracking()
                .AnyAsync(c => c.Id == conversationId, ct);
        }
    }
}
