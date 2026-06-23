using Iris.Api.Authentication;
using Iris.Application.Conversations.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Iris.Api.Hubs;

[Authorize]
public class ChatHub : Hub<IChatClient>
{
    private readonly IConversationQueries _conversationQueries;

    public ChatHub(IConversationQueries conversationQueries)
    {
        _conversationQueries = conversationQueries;
    }

    public async Task JoinConversation(Guid conversationId)
    {
        var userId = Context.User!.GetUserId();
        if (!await _conversationQueries.ExistsForUserAsync(userId, conversationId, Context.ConnectionAborted))
            throw new HubException("Conversation does not exist for this user");

        await Groups.AddToGroupAsync(Context.ConnectionId, GetConversationGroupName(conversationId), Context.ConnectionAborted);
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
