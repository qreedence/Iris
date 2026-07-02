namespace Iris.Domain.Conversations.Entities
{
    public class ConversationTurnRequest
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public Guid UserId { get; set; }
        public string Model { get; set; } = string.Empty;
        public bool ChangeModel { get; set; }

        // Serialized ModelParameters record (jsonb). Kept as a string here so the
        // Domain stays pure — serialization lives in the Infrastructure store.
        public string? ModelParameters { get; set; }

        public ConversationTurnStatus Status { get; set; }
        public int AttemptCount { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ClaimedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? LastError { get; set; }
    }
}
