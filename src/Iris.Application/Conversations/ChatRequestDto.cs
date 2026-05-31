using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public record ChatRequestDto(
    string UserMessage,
    string Model,
    bool ChangeModel = false,
    ModelParameters? ModelParameters = null);
