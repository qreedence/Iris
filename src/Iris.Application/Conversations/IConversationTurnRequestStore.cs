using Iris.Domain.Conversations.Entities;

namespace Iris.Application.Conversations;

/// <summary>
/// Durable, Postgres-backed outbox for conversation turn requests. The worker
/// claims rows via short status-update transactions and never holds a transaction
/// across an LLM stream.
/// </summary>
public interface IConversationTurnRequestStore
{
    /// <summary>
    /// Tracks a new Pending row on the current scoped DbContext WITHOUT saving.
    /// INVARIANT: the caller (StartConversationTurnHandler) relies on the event
    /// store's single SaveChangesAsync (via RecordAsync) to commit this row
    /// atomically with the MessageSent event on the same scoped DbContext.
    /// </summary>
    void AddPending(ConversationTurnRequest request);

    /// <summary>
    /// Atomically claims up to <paramref name="maxCount"/> Pending rows — the
    /// oldest Pending per conversation, skipping any conversation that already has
    /// a Processing row — using FOR UPDATE SKIP LOCKED. Sets Status=Processing,
    /// ClaimedAt=now, AttemptCount+=1 and returns the claimed rows.
    /// </summary>
    Task<IReadOnlyList<ConversationTurnRequest>> ClaimPendingAsync(
        int maxCount,
        int maxAttempts,
        CancellationToken ct = default);

    /// <summary>Marks a claimed row Completed (sets CompletedAt).</summary>
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Marks a row Cancelled (sets CompletedAt).</summary>
    Task MarkCancelledAsync(Guid id, CancellationToken ct = default);

    /// <summary>Resets a row to Pending for retry, recording the last error.</summary>
    Task ResetToPendingAsync(Guid id, string? lastError, CancellationToken ct = default);

    /// <summary>Marks a row Failed (sets CompletedAt), recording the last error.</summary>
    Task MarkFailedAsync(Guid id, string? lastError, CancellationToken ct = default);

    /// <summary>
    /// Resets orphaned Processing rows (ClaimedAt older than <paramref name="claimLease"/>)
    /// that are still under the attempt cap back to Pending. Returns the ids of
    /// orphans that have reached the attempt cap and were marked Failed so the
    /// caller can record the terminal TurnFailed event for each.
    /// </summary>
    Task<IReadOnlyList<ConversationTurnRequest>> RecoverOrphansAsync(
        TimeSpan claimLease,
        int maxAttempts,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the latest active (Pending or Processing) row for a conversation,
    /// or null if none. Used by the cancellation endpoint.
    /// </summary>
    Task<ConversationTurnRequest?> GetLatestActiveAsync(Guid conversationId, CancellationToken ct = default);
}
