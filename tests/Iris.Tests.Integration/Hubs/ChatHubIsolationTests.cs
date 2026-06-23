using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Iris.Api.Hubs;
using Iris.Application.Conversations.Queries;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Iris.Tests.Integration.Hubs;

public class ChatHubIsolationTests
{
    private readonly IConversationQueries _queries = Substitute.For<IConversationQueries>();
    private readonly IGroupManager _groups = Substitute.For<IGroupManager>();

    private ChatHub CreateHub(Guid userId)
    {
        var hub = new ChatHub(_queries);

        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(Guid.NewGuid().ToString());
        context.User.Returns(CreatePrincipal(userId));

        hub.Context = context;
        hub.Groups = _groups;

        return hub;
    }

    private static ClaimsPrincipal CreatePrincipal(Guid userId)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "test@iris.local")
        };
        var identity = new ClaimsIdentity(claims, "Test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task JoinConversation_OwnConversation_AddsToGroup()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        _queries.ExistsForUserAsync(userId, conversationId, Arg.Any<CancellationToken>())
            .Returns(true);

        var hub = CreateHub(userId);

        // Act
        await hub.JoinConversation(conversationId);

        // Assert
        await _groups.Received(1).AddToGroupAsync(
            hub.Context.ConnectionId,
            $"conversation-{conversationId}",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinConversation_OtherUsersConversation_ThrowsHubException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        _queries.ExistsForUserAsync(userId, conversationId, Arg.Any<CancellationToken>())
            .Returns(false);

        var hub = CreateHub(userId);

        // Act
        var act = () => hub.JoinConversation(conversationId);

        // Assert
        await act.Should().ThrowAsync<HubException>();

        await _groups.DidNotReceive().AddToGroupAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinConversation_NonExistentConversation_ThrowsHubException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        _queries.ExistsForUserAsync(userId, conversationId, Arg.Any<CancellationToken>())
            .Returns(false);

        var hub = CreateHub(userId);

        // Act
        var act = () => hub.JoinConversation(conversationId);

        // Assert
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task LeaveConversation_AnyConversation_RemovesFromGroupWithoutError()
    {
        // LeaveConversation is safe/idempotent — removing from a group you never
        // joined is a no-op in SignalR. No ownership check needed; no data leaks.
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var hub = CreateHub(userId);

        // Act — should not throw regardless of ownership
        var act = () => hub.LeaveConversation(conversationId);

        // Assert
        await act.Should().NotThrowAsync();

        await _groups.Received(1).RemoveFromGroupAsync(
            hub.Context.ConnectionId,
            $"conversation-{conversationId}",
            Arg.Any<CancellationToken>());
    }
}
