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

    // Terminal-state writes below are guarded on the current Status so two racing
    // terminal writers (e.g. a user cancel landing just as the stream completes)
    // cannot overwrite each other: the first one wins, the loser's ExecuteUpdate
    // matches zero rows and is silently ignored.

    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        await _db.ConversationTurnRequests
            .Where(r => r.Id == id && r.Status == ConversationTurnStatus.Processing)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, ConversationTurnStatus.Completed)
                    .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow),
                ct);
    }

    public async Task MarkCancelledAsync(Guid id, CancellationToken ct = default)
    {
        // Pending is allowed too: the cancel-before-claim flavour cancels a row the
        // worker has never touched.
        await _db.ConversationTurnRequests
            .Where(r => r.Id == id
                        && (r.Status == ConversationTurnStatus.Pending
                            || r.Status == ConversationTurnStatus.Processing))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, ConversationTurnStatus.Cancelled)
                    .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow),
                ct);
    }

    public async Task ResetToPendingAsync(Guid id, string? lastError, CancellationToken ct = default)
    {
        await _db.ConversationTurnRequests
            .Where(r => r.Id == id && r.Status == ConversationTurnStatus.Processing)
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
            .Where(r => r.Id == id && r.Status == ConversationTurnStatus.Processing)
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
        IReadOnlyCollection<Guid> activeConversationIds,
        CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - claimLease;

        // LEASE vs LIVE STREAMS: a turn actively streaming in THIS process must never
        // be reset by lease expiry, no matter how long the stream runs. The worker
        // passes the set of locally-active conversation ids; we exclude them here.
        // This exclusion is exactly what makes single-instance lease expiry safe: any
        // remaining stale Processing row belongs to a process that is gone. A
        // multi-instance deployment would instead need per-row lease HEARTBEATS (a
        // live worker periodically bumping ClaimedAt) — documented, out of scope.
        var active = activeConversationIds as ISet<Guid> ?? activeConversationIds.ToHashSet();

        var orphans = await _db.ConversationTurnRequests
            .Where(r => r.Status == ConversationTurnStatus.Processing
                        && r.ClaimedAt != null
                        && r.ClaimedAt < cutoff)
            .ToListAsync(ct);

        orphans = orphans.Where(o => !active.Contains(o.ConversationId)).ToList();

        if (orphans.Count == 0)
            return [];

        var atCap = new List<ConversationTurnRequest>();

        foreach (var orphan in orphans)
        {
            if (orphan.AttemptCount >= maxAttempts)
            {
                // Do NOT mutate here. Return the candidate so the caller records the
                // terminal TurnFailed event FIRST, then flips the row Failed. A crash
                // between those two steps leaves the row Processing at cap → the next
                // recovery tick records a DUPLICATE TurnFailed (audit-only event;
                // missing is worse than duplicated).
                atCap.Add(orphan);
            }
            else
            {
                // Status-guarded reset: a racing terminal write (e.g. a cancel landing
                // as we recover) cannot be resurrected — the WHERE Status==Processing
                // matches zero rows and the reset is silently ignored.
                await _db.ConversationTurnRequests
                    .Where(r => r.Id == orphan.Id && r.Status == ConversationTurnStatus.Processing)
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(r => r.Status, ConversationTurnStatus.Pending)
                            .SetProperty(r => r.ClaimedAt, (DateTimeOffset?)null)
                            .SetProperty(r => r.LastError, "interrupted"),
                        ct);
            }
        }

        return atCap;
    }

    public async Task<IReadOnlyList<ConversationTurnRequest>> GetActiveAsync(Guid conversationId, CancellationToken ct = default)
    {
        return await _db.ConversationTurnRequests
            .Where(r => r.ConversationId == conversationId
                        && (r.Status == ConversationTurnStatus.Pending
                            || r.Status == ConversationTurnStatus.Processing))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }
}
