using System.Collections.Concurrent;
using Iris.Application.Conversations;

namespace Iris.Api.Conversations;

/// <summary>
/// Singleton registry of in-flight turn cancellation sources keyed by
/// ConversationId. A conversation can have at most one Processing turn at a time
/// (guaranteed by the claim query), so a single CTS per conversation is correct.
/// </summary>
public class ActiveTurnRegistry : IActiveTurnRegistry
{
    private readonly ConcurrentDictionary<Guid, Entry> _active = new();

    private sealed class Entry(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public bool UserCancelled;
    }

    public void Register(Guid conversationId, CancellationTokenSource cts)
    {
        _active[conversationId] = new Entry(cts);
    }

    public void Remove(Guid conversationId)
    {
        _active.TryRemove(conversationId, out _);
    }

    public bool TryCancel(Guid conversationId)
    {
        if (!_active.TryGetValue(conversationId, out var entry))
            return false;

        // Mark the source BEFORE firing so a concurrent OCE catch in the orchestrator
        // observes the user-cancel flag.
        entry.UserCancelled = true;

        try
        {
            entry.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished and disposed its CTS between lookup and cancel;
            // treat as "nothing to cancel".
            return false;
        }

        return true;
    }

    public bool WasUserCancelled(Guid conversationId)
    {
        return _active.TryGetValue(conversationId, out var entry) && entry.UserCancelled;
    }

    public IReadOnlyCollection<Guid> ActiveConversationIds => _active.Keys.ToArray();
}
