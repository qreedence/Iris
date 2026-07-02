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

    /// <summary>Removes the registration for a finished turn.</summary>
    void Remove(Guid conversationId);

    /// <summary>
    /// Cancels the in-flight turn for the conversation if one is registered.
    /// Returns true if a turn was found and cancelled.
    /// </summary>
    bool TryCancel(Guid conversationId);
}
