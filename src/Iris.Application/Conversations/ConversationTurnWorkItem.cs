using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public record ConversationTurnWorkItem(
    Guid ConversationId,
    string Model,
    ModelParameters? ModelParameters);
