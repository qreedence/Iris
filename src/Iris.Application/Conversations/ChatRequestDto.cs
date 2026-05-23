using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public record ChatRequestDto(
    string UserMessage,
    string Model,
    string? SystemPrompt = null,
    ModelParameters? ModelParameters = null);
