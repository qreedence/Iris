using Iris.Application.AiIntegration.Models;
using MediatR;

namespace Iris.Application.Conversations.Commands.StartConversationTurn;

public record StartConversationTurnCommand(
    Guid ConversationId,
    string UserMessage,
    string Model,
    ModelParameters? ModelParameters) : IRequest<Unit>;
