namespace Iris.Application.Conversations;

public record CreateConversationRequest(
    Guid PersonaId,
    string Title);
