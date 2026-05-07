using Iris.Application.AiIntegration.Models;
using MediatR;

namespace Iris.Application.Conversations.Commands.Chat
{
    public record ChatCommand(
        Guid ConversationId, 
        string UserMessage,
        string Model, 
        string? SystemPrompt = null, 
        ModelParameters? ModelParameters = null
    ) : IRequest<ChatResponse>;
}