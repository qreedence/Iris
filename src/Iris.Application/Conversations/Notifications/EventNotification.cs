using Iris.Application.Conversations;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Notifications
{
    public record EventNotification<T>(RecordedEvent RecordedEvent) : INotification
        where T : ConversationEvent
    {
        public T Event => RecordedEvent.Event is T typedEvent
            ? typedEvent
            : throw new InvalidOperationException(
                $"Recorded event contains {RecordedEvent.Event.GetType().Name}, not {typeof(T).Name}.");

        public long SequenceNumber => RecordedEvent.SequenceNumber;
        public Guid AggregateId => RecordedEvent.AggregateId;
        public Guid CommandId => RecordedEvent.CommandId;
        public DateTimeOffset OccurredAt => RecordedEvent.OccurredAt;
    }
}
