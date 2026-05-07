using Iris.Domain.AiIntegration;
using MediatR;

namespace Iris.Application.Conversations.Commands.SendMessage
{
    public record SendMessageCommand(Guid ConversationId, string Content, ChatRole Role) : IRequest<Guid>;
}
