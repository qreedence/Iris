using Iris.Application.AiIntegration.Models;

namespace Iris.Application.Conversations;

public record ConversationTurnWorkItem
{
    public Guid UserId { get; init; }
    public Guid ConversationId { get; init; }
    public string Model { get; init; } = string.Empty;
    public bool ChangeModel { get; init; }
    public ModelParameters? ModelParameters { get; init; }
}