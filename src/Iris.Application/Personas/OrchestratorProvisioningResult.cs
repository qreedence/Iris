namespace Iris.Application.Personas;

public record OrchestratorProvisioningResult(
    Guid PersonaId,
    Guid ConversationId);
