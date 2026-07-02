namespace Iris.Api.Hubs;

internal static class ConversationGroups
{
    public static string For(Guid conversationId) => $"conversation-{conversationId}";
}
