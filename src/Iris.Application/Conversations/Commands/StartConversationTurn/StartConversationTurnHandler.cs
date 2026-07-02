using System.Text.Json;
using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.StartConversationTurn;

public class StartConversationTurnHandler : IRequestHandler<StartConversationTurnCommand, Unit>
{
    private readonly IConversationQueries _conversationQueries;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly IConversationTurnRequestStore _turnRequestStore;
    private readonly ITurnDoorbell _doorbell;

    public StartConversationTurnHandler(
        IConversationQueries conversationQueries,
        IConversationEventRecorder eventRecorder,
        IConversationTurnRequestStore turnRequestStore,
        ITurnDoorbell doorbell)
    {
        _conversationQueries = conversationQueries;
        _eventRecorder = eventRecorder;
        _turnRequestStore = turnRequestStore;
        _doorbell = doorbell;
    }

    public async Task<Unit> Handle(StartConversationTurnCommand command, CancellationToken ct)
    {
        if (command.ConversationId == Guid.Empty)
            throw new ValidationException("ConversationId can not be empty.");

        if (string.IsNullOrWhiteSpace(command.UserMessage))
            throw new ValidationException("Content can not be empty.");

        if (string.IsNullOrWhiteSpace(command.Model))
            throw new ValidationException("Model can not be empty.");

        // Ownership check via the read model rather than a full event-stream load:
        // the read model is projected synchronously in-process, so there is no lag,
        // and this saves a full stream load + deserialization on every message.
        var exists = await _conversationQueries.ExistsForUserAsync(command.ConversationId, ct);
        if (!exists)
            throw new NotFoundException("Conversation does not exist.");

        var message = new MessageSent(
            Guid.NewGuid(),
            command.ConversationId,
            command.UserMessage,
            ChatRole.User);

        // INVARIANT: AddPending must run BEFORE RecordAsync. Both operate on the
        // same scoped DbContext, so the event store's single SaveChangesAsync
        // commits the queue row and the MessageSent event in one transaction —
        // the turn is never durably enqueued without its user message, or vice
        // versa. If the append fails, the tracked-but-unsaved row is discarded.
        var turnRequest = new ConversationTurnRequest
        {
            Id = Guid.NewGuid(),
            ConversationId = command.ConversationId,
            UserId = command.UserId,
            // Links the row to ITS MessageSent event so the worker's retry
            // idempotency check inspects this exact turn, not just the latest one.
            MessageId = message.Id,
            Model = command.Model,
            ChangeModel = command.ChangeModel,
            ModelParameters = command.ModelParameters is null
                ? null
                : JsonSerializer.Serialize(command.ModelParameters),
            Status = ConversationTurnStatus.Pending,
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _turnRequestStore.AddPending(turnRequest);

        await _eventRecorder.RecordAsync(command.ConversationId, [message], ct);

        // Ring AFTER the commit so the worker cannot poll, miss the not-yet-visible
        // row, and then wait a full PollInterval before noticing it.
        _doorbell.Ring();

        return Unit.Value;
    }
}
