using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public interface IChatStreamOrchestrator
{
    Task StreamAsync(
        Guid conversationId,
        string model,
        string? systemPrompt,
        ModelParameters? modelParameters,
        CancellationToken ct);
}
