namespace Iris.Domain.Conversations.Entities
{
    public class ConversationReadModel
    {
        public Guid Id { get; set; }
        public Guid PersonaId { get; set; }
        public string Title { get; set; } = string.Empty;
        public int MessageCount { get; set; }
        public DateTimeOffset? LastMessageAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
