namespace Iris.Application.Conversations;

public interface IChatStreamNotifier
{
    Task SendChunkAsync(Guid conversationId, ChatStreamChunkDto chunk, CancellationToken ct = default);

    Task SendErrorAsync(Guid conversationId, string errorCode, string message, CancellationToken ct = default);

    Task SendCompletedAsync(Guid conversationId, CancellationToken ct = default);
}