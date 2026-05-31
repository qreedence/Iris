using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public interface IConversationTurnPreparer
{
    Task<PreparedConversationTurn> PrepareAsync(
        Guid conversationId,
        string requestedModel,
        ModelParameters? modelParameters,
        CancellationToken ct = default);
}
