namespace Iris.Application.AiIntegration.Models
{
    public record ChatRequest
    (
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        string? SystemPrompt = null,
        ModelParameters? ModelParameters = null
    );
}
