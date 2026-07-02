using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.StartConversationTurn;

public class StartConversationTurnHandler : IRequestHandler<StartConversationTurnCommand, Unit>
{
    private readonly IConversationQueries _conversationQueries;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly IConversationTurnQueue _turnQueue;

    public StartConversationTurnHandler(
        IConversationQueries conversationQueries,
        IConversationEventRecorder eventRecorder,
        IConversationTurnQueue turnQueue)
    {
        _conversationQueries = conversationQueries;
        _eventRecorder = eventRecorder;
        _turnQueue = turnQueue;
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

        await _eventRecorder.RecordAsync(command.ConversationId, [message], ct);

        await _turnQueue.EnqueueAsync(
            new ConversationTurnWorkItem
            {
                UserId = command.UserId,
                ConversationId = command.ConversationId,
                Model = command.Model,
                ChangeModel = command.ChangeModel,
                ModelParameters = command.ModelParameters,
            },
            ct);

        return Unit.Value;
    }
}
