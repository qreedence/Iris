using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Iris.Api.Hubs;

[Authorize]
public class ChatHub : Hub<IChatClient>
{
    public Task JoinConversation(Guid conversationId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, GetConversationGroupName(conversationId));
    }

    public Task LeaveConversation(Guid conversationId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GetConversationGroupName(conversationId));
    }

    private static string GetConversationGroupName(Guid conversationId)
    {
        return $"conversation-{conversationId}";
    }
}
