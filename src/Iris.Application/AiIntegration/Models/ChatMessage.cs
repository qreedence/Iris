using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;

namespace Iris.Application.AiIntegration.Models
{
    public record ChatMessage(ChatRole Role, IReadOnlyList<MessageContentBlock> ContentBlocks)
    {
        public string VisibleText => MessageContentBlocks.ToVisibleText(ContentBlocks);
    }
}