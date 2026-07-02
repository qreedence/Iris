using System.Collections.Concurrent;
using Iris.Application.Conversations.Notifications;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations;

public class ConversationEventRecorder : IConversationEventRecorder
{
    private static readonly ConcurrentDictionary<Type, Func<RecordedEvent, INotification>> NotificationFactories = new();

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

        var commandId = Guid.NewGuid();
        var recordedEvents = await _eventStore.AppendAsync(aggregateId, eventList, commandId, ct);

        foreach (var recordedEvent in recordedEvents)
        {
            var notification = CreateNotification(recordedEvent);
            await _publisher.Publish(notification, ct);
        }

        return recordedEvents;
    }

    private static INotification CreateNotification(RecordedEvent recordedEvent)
    {
        var factory = NotificationFactories.GetOrAdd(recordedEvent.Event.GetType(), BuildFactory);
        return factory(recordedEvent);
    }

    private static Func<RecordedEvent, INotification> BuildFactory(Type eventType)
    {
        var notificationType = typeof(EventNotification<>).MakeGenericType(eventType);
        return recordedEvent => (INotification)Activator.CreateInstance(notificationType, recordedEvent)!;
    }
}
