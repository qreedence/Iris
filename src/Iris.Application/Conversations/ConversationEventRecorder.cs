using Iris.Application.Conversations.Notifications;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations;

public class ConversationEventRecorder : IConversationEventRecorder
{
    private readonly IEventStore _eventStore;
    private readonly IPublisher _publisher;

    public ConversationEventRecorder(IEventStore eventStore, IPublisher publisher)
    {
        _eventStore = eventStore;
        _publisher = publisher;
    }

    public async Task<IReadOnlyList<RecordedEvent>> RecordAsync(
        Guid aggregateId,
        IEnumerable<ConversationEvent> events,
        CancellationToken ct = default)
    {
        var eventList = events.ToList();

        foreach (var evt in eventList)
        {
            EnsureSupportedEvent(evt);
        }

        var commandId = Guid.NewGuid();
        var recordedEvents = await _eventStore.AppendAsync(aggregateId, eventList, commandId, ct);

        foreach (var recordedEvent in recordedEvents)
        {
            await PublishAsync(recordedEvent, ct);
        }

        return recordedEvents;
    }

    private async Task PublishAsync(RecordedEvent recordedEvent, CancellationToken ct)
    {
        switch (recordedEvent.Event)
        {
            case ConversationCreated:
                await _publisher.Publish(new EventNotification<ConversationCreated>(recordedEvent), ct);
                break;
            case MessageSent:
                await _publisher.Publish(new EventNotification<MessageSent>(recordedEvent), ct);
                break;
            case AssistantResponseCompleted:
                await _publisher.Publish(new EventNotification<AssistantResponseCompleted>(recordedEvent), ct);
                break;
            case TurnCompleted:
                await _publisher.Publish(new EventNotification<TurnCompleted>(recordedEvent), ct);
                break;
            case TurnFailed:
                await _publisher.Publish(new EventNotification<TurnFailed>(recordedEvent), ct);
                break;
            case TurnCancelled:
                await _publisher.Publish(new EventNotification<TurnCancelled>(recordedEvent), ct);
                break;
            case ModelChanged:
                await _publisher.Publish(new EventNotification<ModelChanged>(recordedEvent), ct);
                break;
            case ConversationArchived:
                await _publisher.Publish(new EventNotification<ConversationArchived>(recordedEvent), ct);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown conversation event type '{recordedEvent.Event.GetType().Name}'.");
        }
    }

    private static void EnsureSupportedEvent(ConversationEvent evt)
    {
        if (evt is ConversationCreated
            or MessageSent
            or AssistantResponseCompleted
            or TurnCompleted
            or TurnFailed
            or TurnCancelled
            or ModelChanged
            or ConversationArchived)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Unknown conversation event type '{evt.GetType().Name}'.");
    }
}
