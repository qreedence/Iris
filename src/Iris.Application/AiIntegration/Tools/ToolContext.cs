namespace Iris.Application.AiIntegration.Tools;

public record ToolContext(
    Guid UserId,
    Guid PersonaId,
    Guid ConversationId);
