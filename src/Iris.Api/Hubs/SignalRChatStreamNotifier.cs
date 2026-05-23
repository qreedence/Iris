using Iris.Application.Conversations;
using Microsoft.AspNetCore.SignalR;

namespace Iris.Api.Hubs;

public class SignalRChatStreamNotifier : IChatStreamNotifier
{
    private readonly IHubContext<ChatHub, IChatClient> _hubContext;

    public SignalRChatStreamNotifier(IHubContext<ChatHub, IChatClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task SendChunkAsync(Guid conversationId, string content, CancellationToken ct)
    {
        return _hubContext.Clients
            .Group(GetConversationGroupName(conversationId))
            .ReceiveChunk(content);
    }

    public Task SendErrorAsync(Guid conversationId, string errorCode, string message, CancellationToken ct)
    {
        return _hubContext.Clients
            .Group(GetConversationGroupName(conversationId))
            .ReceiveError(errorCode, message);
    }

    public Task SendCompletedAsync(Guid conversationId, CancellationToken ct)
    {
        return _hubContext.Clients
            .Group(GetConversationGroupName(conversationId))
            .StreamCompleted();
    }

    private static string GetConversationGroupName(Guid conversationId)
    {
        return $"conversation-{conversationId}";
    }
}
