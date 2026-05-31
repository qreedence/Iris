using Iris.Application.AiIntegration.Models;
using MediatR;

namespace Iris.Application.Conversations.Commands.StartConversationTurn;

public record StartConversationTurnCommand(
    Guid ConversationId,
    string UserMessage,
    string Model,
    bool ChangeModel,
    ModelParameters? ModelParameters) : IRequest<Unit>;
