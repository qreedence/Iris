namespace Iris.Application.Conversations;

public interface IChatStreamNotifier
{
    Task SendChunkAsync(Guid conversationId, string content, CancellationToken ct);

    Task SendErrorAsync(Guid conversationId, string errorCode, string message, CancellationToken ct);

    Task SendCompletedAsync(Guid conversationId, CancellationToken ct);
}
