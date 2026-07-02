using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Entities;
using MediatR;

namespace Iris.Application.Conversations.Commands.CancelConversationTurn;

public class CancelConversationTurnHandler : IRequestHandler<CancelConversationTurnCommand, Unit>
{
    private readonly IConversationQueries _conversationQueries;
    private readonly IConversationTurnRequestStore _turnRequestStore;
    private readonly IActiveTurnRegistry _activeTurns;

    public CancelConversationTurnHandler(
        IConversationQueries conversationQueries,
        IConversationTurnRequestStore turnRequestStore,
        IActiveTurnRegistry activeTurns)
    {
        _conversationQueries = conversationQueries;
        _turnRequestStore = turnRequestStore;
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

        foreach (var turn in active)
        {
            if (turn.Status == ConversationTurnStatus.Pending)
            {
                // Never started — mark Cancelled directly so the worker skips it.
                await _turnRequestStore.MarkCancelledAsync(turn.Id, ct);
            }
            else if (!firedCts)
            {
                // The single Processing turn (at most one per conversation) — mark
                // Cancelled and fire the in-process CTS. The worker's dispatch loop
                // sees the cancellation and the orchestrator records TurnCancelled.
                await _turnRequestStore.MarkCancelledAsync(turn.Id, ct);
                _activeTurns.TryCancel(command.ConversationId);
                firedCts = true;
            }
        }

        return Unit.Value;
    }
}
