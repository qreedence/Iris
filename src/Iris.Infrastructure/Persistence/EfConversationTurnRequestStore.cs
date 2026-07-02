using Iris.Application.Conversations;
using Iris.Domain.Conversations.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Iris.Infrastructure.Persistence;

public class EfConversationTurnRequestStore : IConversationTurnRequestStore
{
    private readonly AppDbContext _db;

    public EfConversationTurnRequestStore(AppDbContext db)
    {
        _db = db;
    }

    public void AddPending(ConversationTurnRequest request)
    {
        // Tracks WITHOUT saving. The event store's SaveChangesAsync (called by
        // RecordAsync on the same scoped DbContext) commits this row atomically
        // with the MessageSent event.
        _db.ConversationTurnRequests.Add(request);
    }

    public async Task<IReadOnlyList<ConversationTurnRequest>> ClaimPendingAsync(
        int maxCount,
        int maxAttempts,
        CancellationToken ct = default)
    {
        if (maxCount <= 0)
            return [];

        // Claim the oldest Pending row per conversation, skipping any conversation
        // that already has a Processing row. FOR UPDATE SKIP LOCKED makes the inner
        // selection safe under concurrency; DISTINCT ON keeps it to one row per
        // conversation. The worker is a singleton and claims inside a single loop
        // iteration, so claim rounds are inherently serial — but SKIP LOCKED plus
        // the "no Processing row for this conversation" guard also make it safe
        // even if a second claimer ran concurrently.
        const string sql =
            """
            UPDATE conversation_turn_requests AS t
            SET "Status" = 'Processing',
                "ClaimedAt" = now(),
                "AttemptCount" = t."AttemptCount" + 1
            FROM (
                SELECT DISTINCT ON (c."ConversationId") c."Id"
                FROM conversation_turn_requests AS c
                WHERE c."Status" = 'Pending'
                  AND c."AttemptCount" < @maxAttempts
                  AND NOT EXISTS (
                      SELECT 1 FROM conversation_turn_requests AS p
                      WHERE p."ConversationId" = c."ConversationId"
                        AND p."Status" = 'Processing'
                  )
                ORDER BY c."ConversationId", c."CreatedAt"
                FOR UPDATE OF c SKIP LOCKED
                LIMIT @maxCount
            ) AS claimed
            WHERE t."Id" = claimed."Id"
            RETURNING t.*;
            """;

        var rows = await _db.ConversationTurnRequests
            .FromSqlRaw(
                sql,
                new NpgsqlParameter("maxCount", maxCount),
                new NpgsqlParameter("maxAttempts", maxAttempts))
            .AsNoTracking()
            .ToListAsync(ct);

        return rows;
    }

    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        await _db.ConversationTurnRequests
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, ConversationTurnStatus.Completed)
                    .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow),
                ct);
    }

    public async Task MarkCancelledAsync(Guid id, CancellationToken ct = default)
    {
        await _db.ConversationTurnRequests
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, ConversationTurnStatus.Cancelled)
                    .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow),
                ct);
    }

    public async Task ResetToPendingAsync(Guid id, string? lastError, CancellationToken ct = default)
    {
        await _db.ConversationTurnRequests
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, ConversationTurnStatus.Pending)
                    .SetProperty(r => r.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(r => r.LastError, lastError),
                ct);
    }

    public async Task MarkFailedAsync(Guid id, string? lastError, CancellationToken ct = default)
    {
        await _db.ConversationTurnRequests
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, ConversationTurnStatus.Failed)
                    .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow)
                    .SetProperty(r => r.LastError, lastError),
                ct);
    }

    public async Task<IReadOnlyList<ConversationTurnRequest>> RecoverOrphansAsync(
        TimeSpan claimLease,
        int maxAttempts,
        CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - claimLease;

        var orphans = await _db.ConversationTurnRequests
            .Where(r => r.Status == ConversationTurnStatus.Processing
                        && r.ClaimedAt != null
                        && r.ClaimedAt < cutoff)
            .ToListAsync(ct);

        if (orphans.Count == 0)
            return [];

        var failed = new List<ConversationTurnRequest>();

        foreach (var orphan in orphans)
        {
            if (orphan.AttemptCount >= maxAttempts)
            {
                orphan.Status = ConversationTurnStatus.Failed;
                orphan.CompletedAt = DateTimeOffset.UtcNow;
                orphan.LastError = "interrupted";
                failed.Add(orphan);
            }
            else
            {
                orphan.Status = ConversationTurnStatus.Pending;
                orphan.ClaimedAt = null;
                orphan.LastError = "interrupted";
            }
        }

        await _db.SaveChangesAsync(ct);

        return failed;
    }

    public async Task<ConversationTurnRequest?> GetLatestActiveAsync(Guid conversationId, CancellationToken ct = default)
    {
        return await _db.ConversationTurnRequests
            .Where(r => r.ConversationId == conversationId
                        && (r.Status == ConversationTurnStatus.Pending
                            || r.Status == ConversationTurnStatus.Processing))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
