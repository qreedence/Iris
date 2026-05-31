using Iris.Application.AiIntegration.Models;
using Iris.Domain.Conversations.Events;

namespace Iris.Application.Conversations;

public record PreparedConversationTurn(
    ChatRequest ChatRequest,
    IReadOnlyList<ConversationEvent> PreStreamEvents);
