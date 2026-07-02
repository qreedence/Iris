using Iris.Application.Conversations;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Helpers;

/// <summary>
/// Seeds conversation messages directly through <see cref="IConversationEventRecorder"/>,
/// bypassing the (now-deleted) SendMessageCommand. Behaviorally identical to what
/// SendMessageHandler did — append a MessageSent event and publish it to projectors —
/// minus its redundant validation.
/// </summary>
public static class ConversationSeeder
{
    public static async Task SendMessageAsync(
        IServiceProvider services,
        Guid conversationId,
        string content,
        ChatRole role = ChatRole.User,
        Guid? overrideUserId = null,
        CancellationToken ct = default)
    {
        using var scope = services.CreateScope();

        if (overrideUserId is { } userId)
        {
            var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
            userService.OverrideUserId = userId;
        }

        var eventRecorder = scope.ServiceProvider.GetRequiredService<IConversationEventRecorder>();
        var message = new MessageSent(Guid.NewGuid(), conversationId, content, role);
        await eventRecorder.RecordAsync(conversationId, [message], ct);
    }
}
