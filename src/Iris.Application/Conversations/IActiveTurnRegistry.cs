namespace Iris.Application.Conversations;

/// <summary>
/// Tracks the in-process cancellation source for each conversation whose turn is
/// currently being streamed. The worker registers a turn's linked
/// CancellationTokenSource when it starts streaming and removes it when the turn
/// ends; the cancellation endpoint uses TryCancel to fire it.
/// </summary>
public interface IActiveTurnRegistry
{
    /// <summary>Registers the cancellation source for an in-flight turn.</summary>
    void Register(Guid conversationId, CancellationTokenSource cts);

    /// <summary>
    /// Removes the registration for a finished turn, clearing any user-cancelled flag.
    /// </summary>
    void Remove(Guid conversationId);

    /// <summary>
    /// Cancels the in-flight turn for the conversation if one is registered, marking
    /// the cancellation source as USER-initiated. Returns true if a turn was found
    /// and cancelled.
    /// </summary>
    bool TryCancel(Guid conversationId);

    /// <summary>
    /// Whether the in-flight turn for this conversation was cancelled by a user
    /// (via <see cref="TryCancel"/>) rather than by host shutdown. The orchestrator
    /// uses this to distinguish a "stop generating" cancel (record TurnCancelled)
    /// from a shutdown-interrupt (record nothing, leave the row for orphan recovery).
    /// </summary>
    bool WasUserCancelled(Guid conversationId);

    /// <summary>
    /// The conversation ids of every turn currently streaming in THIS process.
    /// Orphan recovery excludes these so a genuinely long live stream is never reset
    /// out from under itself.
    /// </summary>
    IReadOnlyCollection<Guid> ActiveConversationIds { get; }
}
