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

    public Task SendChunkAsync(Guid conversationId, string content, CancellationToken ct = default)
    {
        return _hubContext.Clients
            .Group(ConversationGroups.For(conversationId))
            .ReceiveChunk(content);
    }

    public Task SendErrorAsync(Guid conversationId, string errorCode, string message, CancellationToken ct = default)
    {
        return _hubContext.Clients
            .Group(ConversationGroups.For(conversationId))
            .ReceiveError(errorCode, message);
    }

    public Task SendCompletedAsync(Guid conversationId, CancellationToken ct = default)
    {
        return _hubContext.Clients
            .Group(ConversationGroups.For(conversationId))
            .StreamCompleted();
    }
}
