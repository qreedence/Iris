using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public interface IChatStreamOrchestrator
{
    Task StreamAsync(
        Guid conversationId,
        string model,
        bool changeModel,
        ModelParameters? modelParameters,
        CancellationToken ct = default);
}
