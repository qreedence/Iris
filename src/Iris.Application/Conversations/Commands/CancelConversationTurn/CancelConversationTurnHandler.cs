using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.CancelConversationTurn;

public class CancelConversationTurnHandler : IRequestHandler<CancelConversationTurnCommand, Unit>
{
    private readonly IConversationQueries _conversationQueries;
    private readonly IConversationTurnRequestStore _turnRequestStore;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly IActiveTurnRegistry _activeTurns;

    public CancelConversationTurnHandler(
        IConversationQueries conversationQueries,
        IConversationTurnRequestStore turnRequestStore,
        IConversationEventRecorder eventRecorder,
        IActiveTurnRegistry activeTurns)
    {
        _conversationQueries = conversationQueries;
        _turnRequestStore = turnRequestStore;
        _eventRecorder = eventRecorder;
        _activeTurns = activeTurns;
    }

    public async Task<Unit> Handle(CancelConversationTurnCommand command, CancellationToken ct)
    {
        // Ownership is enforced by the read-model query filter via ExistsForUserAsync,
        // mirroring StartConversationTurnHandler.
        var exists = await _conversationQueries.ExistsForUserAsync(command.ConversationId, ct);
        if (!exists)
            throw new NotFoundException("Conversation does not exist.");

        var active = await _turnRequestStore.GetActiveAsync(command.ConversationId, ct);

        // No active turn: treat as an idempotent no-op (the endpoint returns 202).
        // This lets clients retry "stop generating" without racing the worker into
        // a 409 when the turn has just finished.
        if (active.Count == 0)
            return Unit.Value;

        var firedCts = false;

        // Cancel EVERY active turn, not just the newest: with turn A Processing and
        // turn B Pending, "stop generating" must cancel both. Cancelling only the
        // latest could stop the wrong turn.
        foreach (var turn in active)
        {
            if (turn.Status == ConversationTurnStatus.Pending)
            {
                // Never started, so the orchestrator will never run for it — mark
                // Cancelled AND record its own terminal TurnCancelled event (stamped
                // with the turn's MessageId) so the never-started turn is still
                // turn-linkable and terminal. PartialContent is null: nothing streamed.
                await _turnRequestStore.MarkCancelledAsync(turn.Id, ct);
                await _eventRecorder.RecordAsync(
                    command.ConversationId,
                    [new TurnCancelled(command.ConversationId, PartialContent: null, MessageId: turn.MessageId)],
                    ct);
            }
            else if (!firedCts)
            {
                // The single Processing turn (at most one per conversation) — mark
                // Cancelled and fire the in-process CTS. The worker's dispatch loop
                // sees the cancellation and the orchestrator records TurnCancelled
                // itself (with partial content and its MessageId).
                await _turnRequestStore.MarkCancelledAsync(turn.Id, ct);
                _activeTurns.TryCancel(command.ConversationId);
                firedCts = true;
            }
        }

        return Unit.Value;
    }
}
