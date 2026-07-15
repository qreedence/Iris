using Iris.Application.AiIntegration.Models;
using Iris.Domain.Conversations.Events;

namespace Iris.Application.Conversations;

public record PreparedConversationTurn(
    Guid PersonaId,
    ChatRequest ChatRequest,
    IReadOnlyList<ConversationEvent> PreStreamEvents,
    int PriorInputTokens = 0,
    int PriorOutputTokens = 0);
