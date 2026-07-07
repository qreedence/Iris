using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;

namespace Iris.Domain.Conversations.Entities
{
    public class ConversationMessage
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public ChatRole Role { get; set; }
        public List<MessageContentBlock> ContentBlocks { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; }
    }
}