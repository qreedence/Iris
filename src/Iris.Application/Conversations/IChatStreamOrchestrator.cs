using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public interface IChatStreamOrchestrator
{
    Task StreamAsync(
        Guid userId,
        Guid conversationId,
        Guid messageId,
        string model,
        bool changeModel,
        ModelParameters? modelParameters,
        CancellationToken ct = default);
}
