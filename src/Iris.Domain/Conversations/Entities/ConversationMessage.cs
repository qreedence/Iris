using Iris.Domain.AiIntegration;

namespace Iris.Domain.Conversations.Entities
{
    public class ConversationMessage
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public ChatRole Role { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
