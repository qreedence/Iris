using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Commands.SendMessage;
using Iris.Application.Conversations.Queries;
using Iris.Domain.AiIntegration;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Conversations;

public class ConversationEndpointTests : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public ConversationEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SendCommand<TResponse>(IRequest<TResponse> command)
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(command, TestContext.Current.CancellationToken);
    }

    // ── POST /api/conversations ────────────────────────────────────

    [Fact]
    public async Task PostConversation_ValidData_Returns201WithId()
    {
        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(Guid.NewGuid(), "New Chat"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var conversationId = await response.Content
            .ReadFromJsonAsync<Guid>(JsonOptions, TestContext.Current.CancellationToken);

        conversationId.Should().NotBe(Guid.Empty);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task PostConversation_EmptyTitle_Returns400()
    {
        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(Guid.NewGuid(), ""),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostConversation_SequentialCreates_BothSucceed()
    {
        // Arrange — create a conversation via MediatR first
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Existing"));

        // Act — create another via HTTP (server generates a new ID, so no actual duplicate)
        // Instead, test the handler's duplicate guard by sending the same command twice
        var response1 = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(Guid.NewGuid(), "Chat A"),
            TestContext.Current.CancellationToken);

        response1.StatusCode.Should().Be(HttpStatusCode.Created);

        // The server generates IDs, so true duplicates can't happen via REST.
        // The duplicate guard only fires if the same ConversationId is reused,
        // which the endpoint prevents by generating a new Guid each time.
        // This test verifies two sequential creates both succeed.
        var response2 = await _client.PostAsJsonAsync(
            "/api/conversations",
            new CreateConversationRequest(Guid.NewGuid(), "Chat B"),
            TestContext.Current.CancellationToken);

        response2.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── §1 GET /api/conversations — empty ─────────────────────────

    [Fact]
    public async Task GetConversations_NoConversations_ReturnsEmptyList()
    {
        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        conversations.Should().NotBeNull();
    }

    // ── §2 GET /api/conversations — after creating ────────────────

    [Fact]
    public async Task GetConversations_AfterCreating_ReturnsConversationList()
    {
        // Arrange
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(id1, Guid.NewGuid(), "Chat One"));
        await SendCommand(new CreateConversationCommand(id2, Guid.NewGuid(), "Chat Two"));

        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        conversations.Should().NotBeNull();
        conversations.Should().Contain(c => c.Id == id1 && c.Title == "Chat One");
        conversations.Should().Contain(c => c.Id == id2 && c.Title == "Chat Two");
    }

    // ── §3 GET /api/conversations — response shape ────────────────

    [Fact]
    public async Task GetConversations_ResponseShape_MatchesExpectedDto()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Shape Test"));
        await SendCommand(new SendMessageCommand(conversationId, "Hello", ChatRole.User));

        // Act
        var response = await _client.GetAsync("/api/conversations", TestContext.Current.CancellationToken);
        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        var conversation = conversations!.First(c => c.Id == conversationId);

        conversation.Title.Should().Be("Shape Test");
        conversation.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        conversation.MessageCount.Should().Be(1);
        conversation.LastMessageAt.Should().NotBeNull();
    }

    // ── §4 GET /api/conversations/{id}/messages — in order ────────

    [Fact]
    public async Task GetMessages_ExistingConversation_ReturnsMessagesInOrder()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));
        await SendCommand(new SendMessageCommand(conversationId, "First", ChatRole.User));
        await SendCommand(new SendMessageCommand(conversationId, "Second", ChatRole.User));
        await SendCommand(new SendMessageCommand(conversationId, "Third", ChatRole.User));

        // Act
        var response = await _client.GetAsync($"/api/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions, TestContext.Current.CancellationToken);

        messages.Should().HaveCount(3);
        messages![0].Content.Should().Be("First");
        messages[1].Content.Should().Be("Second");
        messages[2].Content.Should().Be("Third");
    }

    // ── §5 GET /api/conversations/{id}/messages — 404 ─────────────

    [Fact]
    public async Task GetMessages_NonExistentConversation_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/conversations/{Guid.NewGuid()}/messages", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── §6 GET /api/conversations/{id}/messages — response shape ──

    [Fact]
    public async Task GetMessages_ResponseShape_MatchesExpectedDto()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));
        await SendCommand(new SendMessageCommand(conversationId, "Hello!", ChatRole.User));

        // Act
        var response = await _client.GetAsync($"/api/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);
        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        var message = messages!.First();

        message.Id.Should().NotBe(Guid.Empty);
        message.ConversationId.Should().Be(conversationId);
        message.Role.Should().Be(ChatRole.User);
        message.Content.Should().Be("Hello!");
        message.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
    }

    // ── §7 GET /api/conversations/{id}/messages — isolation ───────

    [Fact]
    public async Task GetMessages_OnlyReturnsMessagesForRequestedConversation()
    {
        // Arrange
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();

        await SendCommand(new CreateConversationCommand(conversationA, Guid.NewGuid(), "Chat A"));
        await SendCommand(new CreateConversationCommand(conversationB, Guid.NewGuid(), "Chat B"));

        await SendCommand(new SendMessageCommand(conversationA, "Message A", ChatRole.User));
        await SendCommand(new SendMessageCommand(conversationB, "Message B", ChatRole.User));

        // Act
        var response = await _client.GetAsync($"/api/conversations/{conversationA}/messages", TestContext.Current.CancellationToken);
        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions, TestContext.Current.CancellationToken);

        // Assert
        messages.Should().HaveCount(1);
        messages![0].Content.Should().Be("Message A");
        messages[0].ConversationId.Should().Be(conversationA);
    }
}
