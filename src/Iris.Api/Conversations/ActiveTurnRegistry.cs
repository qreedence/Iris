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
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public void Register(Guid conversationId, CancellationTokenSource cts)
    {
        _active[conversationId] = cts;
    }

    public void Remove(Guid conversationId)
    {
        _active.TryRemove(conversationId, out _);
    }

    public bool TryCancel(Guid conversationId)
    {
        if (!_active.TryGetValue(conversationId, out var cts))
            return false;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished and disposed its CTS between lookup and cancel;
            // treat as "nothing to cancel".
            return false;
        }

        return true;
    }
}
