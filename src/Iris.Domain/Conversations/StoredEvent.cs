namespace Iris.Domain.Conversations
{
    public class StoredEvent
    {
        public long SequenceNumber { get; set; }
        public string EventType { get; set; } = string.Empty;
        public Guid AggregateId { get; set; }
        public string EventData { get; set; } = string.Empty;
        public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
        public Guid CommandId { get; set; }
    }
}
