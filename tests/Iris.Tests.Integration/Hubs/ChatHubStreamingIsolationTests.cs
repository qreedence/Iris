using FluentAssertions;
using Iris.Api.Authentication;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Hubs;

public class ChatHubStreamingIsolationTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();

    public ChatHubStreamingIsolationTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private async Task<HubConnection> CreateHubConnectionAsync(Guid userId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                $"{_factory.Server.BaseAddress}hubs/chat",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(userId.ToString());
                })
            .Build();

        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private async Task SendCommandAs<TResponse>(Guid userId, IRequest<TResponse> command)
    {
        using var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = userId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(command, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreatePersonaForUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = userId;
        var personaService = scope.ServiceProvider.GetRequiredService<IPersonaService>();
        var persona = await personaService.CreateAsync(
            userId,
            new CreatePersonaRequest("Test Persona"),
            TestContext.Current.CancellationToken);
        return persona.Id;
    }

    [Fact]
    public async Task JoinConversation_Owner_Succeeds()
    {
        // Arrange
        var personaId = await CreatePersonaForUserAsync(_userA);
        var conversationId = Guid.NewGuid();
        await SendCommandAs(_userA, new CreateConversationCommand(conversationId, _userA, personaId, "User A Chat"));

        await using var connection = await CreateHubConnectionAsync(_userA);

        // Act & Assert — should not throw
        var act = () => connection.InvokeAsync("JoinConversation", conversationId);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task JoinConversation_OtherUser_ThrowsHubException()
    {
        // Arrange
        var personaId = await CreatePersonaForUserAsync(_userA);
        var conversationId = Guid.NewGuid();
        await SendCommandAs(_userA, new CreateConversationCommand(conversationId, _userA, personaId, "User A Chat"));

        await using var connection = await CreateHubConnectionAsync(_userB);

        // Act & Assert
        var act = () => connection.InvokeAsync("JoinConversation", conversationId);
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task Streaming_UserB_DoesNotReceiveUserAChunks()
    {
        // Arrange — create user A's conversation
        var personaId = await CreatePersonaForUserAsync(_userA);
        var conversationId = Guid.NewGuid();
        await SendCommandAs(_userA, new CreateConversationCommand(conversationId, _userA, personaId, "User A Chat"));

        // User A joins their conversation
        await using var connectionA = await CreateHubConnectionAsync(_userA);
        var chunksA = new List<string>();
        connectionA.On<string>("ReceiveChunk", chunk => chunksA.Add(chunk));
        await connectionA.InvokeAsync("JoinConversation", conversationId);

        // User B connects but cannot join user A's conversation
        await using var connectionB = await CreateHubConnectionAsync(_userB);
        var chunksB = new List<string>();
        connectionB.On<string>("ReceiveChunk", chunk => chunksB.Add(chunk));

        // User B tries to join — should be rejected
        try
        {
            await connectionB.InvokeAsync("JoinConversation", conversationId);
        }
        catch (HubException)
        {
            // Expected — user B is not in the group
        }

        // Act — send a chunk to user A's conversation group via the notifier
        using var scope = _factory.Services.CreateScope();
        var notifier = scope.ServiceProvider.GetRequiredService<IChatStreamNotifier>();
        await notifier.SendChunkAsync(conversationId, "secret data", TestContext.Current.CancellationToken);

        // Give SignalR a moment to deliver
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        chunksA.Should().Contain("secret data", "user A should receive chunks for their conversation");
        chunksB.Should().BeEmpty("user B should NOT receive chunks for user A's conversation");
    }
}
