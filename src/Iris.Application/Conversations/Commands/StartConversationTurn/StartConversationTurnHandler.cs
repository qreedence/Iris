using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.StartConversationTurn;

public class StartConversationTurnHandler : IRequestHandler<StartConversationTurnCommand, Unit>
{
    private readonly IEventStore _eventStore;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly IConversationTurnQueue _turnQueue;

    public StartConversationTurnHandler(
        IEventStore eventStore,
        IConversationEventRecorder eventRecorder,
        IConversationTurnQueue turnQueue)
    {
        _eventStore = eventStore;
        _eventRecorder = eventRecorder;
        _turnQueue = turnQueue;
    }

    public async Task<Unit> Handle(StartConversationTurnCommand command, CancellationToken ct)
    {
        if (command.ConversationId == Guid.Empty)
            throw new ValidationException("ConversationId can not be empty.");

        if (string.IsNullOrWhiteSpace(command.UserMessage))
            throw new ValidationException("Content can not be empty.");

        var events = await _eventStore.LoadStreamAsync(command.ConversationId, ct);
        if (events.Count == 0)
            throw new NotFoundException("Conversation does not exist.");

        var message = new MessageSent(
            Guid.NewGuid(),
            command.ConversationId,
            command.UserMessage,
            ChatRole.User);

        await _eventRecorder.RecordAsync(command.ConversationId, [message], ct);

        await _turnQueue.EnqueueAsync(
            new ConversationTurnWorkItem(
                command.ConversationId,
                command.Model,
                command.ModelParameters),
            ct);

        return Unit.Value;
    }
}
