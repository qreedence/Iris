using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public interface IConversationTurnPreparer
{
    Task<PreparedConversationTurn> PrepareAsync(
        Guid userId,
        Guid conversationId,
        Guid messageId,
        string requestedModel,
        bool changeModel,
        ModelParameters? modelParameters,
        CancellationToken ct = default);
}
