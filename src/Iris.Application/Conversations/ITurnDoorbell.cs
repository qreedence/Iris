namespace Iris.Application.Conversations;

/// <summary>
/// A thin wake-up signal for the turn worker. Rung by
/// StartConversationTurnHandler after a new turn request is committed so the
/// worker claims it immediately instead of waiting for the next poll tick.
/// </summary>
public interface ITurnDoorbell
{
    /// <summary>Signals that work may be available. Never blocks.</summary>
    void Ring();

    /// <summary>
    /// Completes when the doorbell is rung, or when <paramref name="ct"/> fires.
    /// A ring that arrives with no waiter is coalesced so the next wait returns
    /// immediately (no missed wake-ups).
    /// </summary>
    Task WaitAsync(CancellationToken ct = default);
}
