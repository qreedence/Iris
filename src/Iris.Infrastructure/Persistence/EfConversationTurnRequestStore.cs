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
        // that already has a Processing row. Postgres forbids FOR UPDATE together
        // with DISTINCT/window functions, so "one row per conversation" is expressed
        // as a plain SELECT with a NOT EXISTS that excludes rows having an earlier
        // eligible sibling in the same conversation — leaving only the oldest. That
        // plain SELECT can safely take FOR UPDATE SKIP LOCKED inside a CTE, and the
        // outer UPDATE ... RETURNING maps the claimed rows back to the entity.
        //
        // Concurrency: the worker is a singleton and claims inside a single loop
        // iteration, so claim rounds are inherently serial. Even if a second claimer
        // ran concurrently, SKIP LOCKED plus the "no Processing row for this
        // conversation" guard prevent two rows for the same conversation from both
        // being claimed.
        const string sql =
            """
            WITH claimed AS (
                SELECT o."Id"
                FROM conversation_turn_requests AS o
                WHERE o."Status" = 'Pending'
                  AND o."AttemptCount" < @maxAttempts
                  AND NOT EXISTS (
                      SELECT 1 FROM conversation_turn_requests AS p
                      WHERE p."ConversationId" = o."ConversationId"
                        AND p."Status" = 'Processing'
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM conversation_turn_requests AS e
                      WHERE e."ConversationId" = o."ConversationId"
                        AND e."Status" = 'Pending'
                        AND e."AttemptCount" < @maxAttempts
                        AND (e."CreatedAt", e."Id") < (o."CreatedAt", o."Id")
                  )
                ORDER BY o."CreatedAt"
                FOR UPDATE SKIP LOCKED
                LIMIT @maxCount
            )
            UPDATE conversation_turn_requests AS t
            SET "Status" = 'Processing',
                "ClaimedAt" = now(),
                "AttemptCount" = t."AttemptCount" + 1
            FROM claimed
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
