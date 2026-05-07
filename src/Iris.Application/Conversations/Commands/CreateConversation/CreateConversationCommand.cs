using MediatR;

namespace Iris.Application.Conversations.Commands.CreateConversation
{
    public record CreateConversationCommand(
        Guid ConversationId,
        Guid PersonaId,
        string Title
    ) : IRequest<Guid>;
}
