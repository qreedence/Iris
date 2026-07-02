using MediatR;

namespace Iris.Application.Conversations.Commands.CancelConversationTurn;

public record CancelConversationTurnCommand : IRequest<Unit>
{
    public Guid ConversationId { get; init; }
}
