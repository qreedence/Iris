using System.Net.Http.Json;
using FluentAssertions;
using Iris.Api.Authentication;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Queries;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Iris.Tests.Integration.Helpers;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Conversations;

/// <summary>
/// Cross-tenant isolation tests proving that service-layer queries,
/// event store access, and read models respect user boundaries.
/// </summary>
public class TenantIsolationTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();

    public TenantIsolationTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private async Task SendCommandAs<TResponse>(Guid userId, IRequest<TResponse> command)
    {
        using var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = userId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(command, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds a MessageSent event as the given user, equivalent to what
    /// SendMessageCommand used to do via SendCommandAs.
    /// </summary>
    private Task SendMessageAs(Guid userId, Guid conversationId, string content, ChatRole role = ChatRole.User)
    {
        return ConversationSeeder.SendMessageAsync(
            _factory.Services, conversationId, content, role, userId, TestContext.Current.CancellationToken);
    }

    private IConversationQueries CreateQueriesAs(IServiceScope scope, Guid userId)
    {
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = userId;
        return scope.ServiceProvider.GetRequiredService<IConversationQueries>();
    }

    /// <summary>
    /// Creates a real persona owned by the given user, so
    /// CreateConversationCommand's persona-ownership check succeeds.
    /// </summary>
    private async Task<Guid> CreatePersonaAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var personaService = scope.ServiceProvider.GetRequiredService<IPersonaService>();
        var persona = await personaService.CreateAsync(
            userId, new CreatePersonaRequest("Iris"), TestContext.Current.CancellationToken);
        return persona.Id;
    }

    // ── ConversationQueries.GetAllAsync isolation ─────────────────

    [Fact]
    public async Task GetAllAsync_ReturnsOnlyCurrentUsersConversations()
    {
        // Arrange
        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();
        var personaA = await CreatePersonaAsync(_userA);
        var personaB = await CreatePersonaAsync(_userB);
        await SendCommandAs(_userA, new CreateConversationCommand(convA, _userA, personaA, "User A Chat"));
        await SendCommandAs(_userB, new CreateConversationCommand(convB, _userB, personaB, "User B Chat"));

        // Act — query as user A
        using var scope = _factory.Services.CreateScope();
        var queries = CreateQueriesAs(scope, _userA);
        var results = await queries.GetAllAsync(0, 50, TestContext.Current.CancellationToken);

        // Assert
        results.Should().Contain(c => c.Id == convA);
        results.Should().NotContain(c => c.Id == convB);
    }

    // ── ConversationQueries.GetMessagesAsync isolation ─────────────

    [Fact]
    public async Task GetMessagesAsync_OtherUsersConversation_ReturnsNull()
    {
        // Arrange
        var convA = Guid.NewGuid();
        var personaA = await CreatePersonaAsync(_userA);
        await SendCommandAs(_userA, new CreateConversationCommand(convA, _userA, personaA, "User A Chat"));
        await SendMessageAs(_userA, convA, "Secret message", ChatRole.User);

        // Act — query as user B
        using var scope = _factory.Services.CreateScope();
        var queries = CreateQueriesAs(scope, _userB);
        var messages = await queries.GetMessagesAsync(convA, 0, 100, TestContext.Current.CancellationToken);

        // Assert
        messages.Should().BeNull("user B should not see user A's conversation");
    }

    // ── ConversationQueries.ExistsForUserAsync isolation ───────────

    [Fact]
    public async Task ExistsForUserAsync_OtherUsersConversation_ReturnsFalse()
    {
        // Arrange
        var convA = Guid.NewGuid();
        var personaA = await CreatePersonaAsync(_userA);
        await SendCommandAs(_userA, new CreateConversationCommand(convA, _userA, personaA, "User A Chat"));

        // Act — check as user B
        using var scope = _factory.Services.CreateScope();
        var queries = CreateQueriesAs(scope, _userB);
        var exists = await queries.ExistsForUserAsync(convA, TestContext.Current.CancellationToken);

        // Assert
        exists.Should().BeFalse();
    }

    // ── Event store — API-level protection ─────────────────────────

    [Fact]
    public async Task EventStore_LoadStream_ReturnsEventsRegardlessOfUser_ProtectedByApiLayer()
    {
        // The event store itself does NOT filter by user — it's a raw append-only
        // store. Protection comes from the API/handler layer preventing unauthorized
        // access. This test documents the design: if you bypass the API and call
        // LoadStream directly, you get events. The security boundary is the controller.

        var convA = Guid.NewGuid();
        var personaA = await CreatePersonaAsync(_userA);
        await SendCommandAs(_userA, new CreateConversationCommand(convA, _userA, personaA, "User A Chat"));
        await SendMessageAs(_userA, convA, "Hello", ChatRole.User);

        // Load as user B directly through the event store (no API layer)
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var events = await store.LoadStreamAsync(convA, TestContext.Current.CancellationToken);

        // Events exist — but user B can never reach this code path through the API
        // because ExistsForUserAsync returns false and the controller returns 404.
        events.Should().HaveCountGreaterThan(0,
            "event store is not user-filtered — protection is at the API boundary");
    }

    [Fact]
    public async Task ChatEndpoint_OtherUsersConversation_EventStoreUnchanged()
    {
        // Prove that the API layer prevents event store writes for other users.
        var convA = Guid.NewGuid();
        var personaA = await CreatePersonaAsync(_userA);
        await SendCommandAs(_userA, new CreateConversationCommand(convA, _userA, personaA, "User A Chat"));

        using var scopeBefore = _factory.Services.CreateScope();
        var storeBefore = scopeBefore.ServiceProvider.GetRequiredService<IEventStore>();
        var countBefore = (await storeBefore.LoadStreamAsync(convA, TestContext.Current.CancellationToken)).Count;

        // Act — user B tries to chat on user A's conversation via HTTP
        using var client = _factory.CreateAuthenticatedClient(_userB);
        await client.PostAsJsonAsync(
            $"/api/conversations/{convA}/chat",
            new ChatRequestDto("Sneaky message", "test/model"),
            TestContext.Current.CancellationToken);

        // Assert — no new events
        using var scopeAfter = _factory.Services.CreateScope();
        var storeAfter = scopeAfter.ServiceProvider.GetRequiredService<IEventStore>();
        var countAfter = (await storeAfter.LoadStreamAsync(convA, TestContext.Current.CancellationToken)).Count;

        countAfter.Should().Be(countBefore, "no events should be appended when another user attempts to chat");
    }

    // ── Projector writes correct UserId ───────────────────────────

    [Fact]
    public async Task ConversationCreatedProjector_SetsUserIdFromEvent()
    {
        // Arrange & Act
        var convId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync(_userA);
        await SendCommandAs(_userA, new CreateConversationCommand(convId, _userA, personaId, "Projected"));

        // Assert — read as the correct user to see it through the filter
        using var scope = _factory.Services.CreateScope();
        var queries = CreateQueriesAs(scope, _userA);
        var conversations = await queries.GetAllAsync(0, 50, TestContext.Current.CancellationToken);

        conversations.Should().Contain(c => c.Id == convId);

        // And NOT visible to another user
        using var scopeB = _factory.Services.CreateScope();
        var queriesB = CreateQueriesAs(scopeB, _userB);
        var conversationsB = await queriesB.GetAllAsync(0, 50, TestContext.Current.CancellationToken);

        conversationsB.Should().NotContain(c => c.Id == convId);
    }
}
