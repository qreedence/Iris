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

        var active = await _turnRequestStore.GetLatestActiveAsync(command.ConversationId, ct);

        // No active turn: treat as an idempotent no-op (the endpoint returns 202).
        // This lets clients retry "stop generating" without racing the worker into
        // a 409 when the turn has just finished.
        if (active is null)
            return Unit.Value;

        if (active.Status == ConversationTurnStatus.Pending)
        {
            // Never claimed — mark Cancelled directly so the worker skips it.
            await _turnRequestStore.MarkCancelledAsync(active.Id, ct);
        }
        else
        {
            // Processing — mark Cancelled and fire the in-process CTS. The worker's
            // dispatch loop sees the cancellation, the orchestrator records
            // TurnCancelled, and the row is already Cancelled.
            await _turnRequestStore.MarkCancelledAsync(active.Id, ct);
            _activeTurns.TryCancel(command.ConversationId);
        }

        return Unit.Value;
    }
}
