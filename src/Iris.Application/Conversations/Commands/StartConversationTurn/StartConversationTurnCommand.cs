using Iris.Application.AiIntegration.Models;
using MediatR;

namespace Iris.Application.Conversations.Commands.StartConversationTurn;

public record StartConversationTurnCommand : IRequest<Unit>
{
    public Guid UserId { get; init; }
    public Guid ConversationId { get; init; }
    public string UserMessage { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public bool ChangeModel { get; init; }
    public ModelParameters? ModelParameters { get; init; }
}
