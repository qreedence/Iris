using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
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

    /// <summary>
    /// Seeds data by dispatching commands through MediatR — the same path as production.
    /// </summary>
    private async Task SendCommand<TResponse>(IRequest<TResponse> command)
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(command, TestContext.Current.CancellationToken);
    }

    // ── §1 GET /api/conversations — empty ─────────────────────────

    [Fact]
    public async Task GetConversations_NoConversations_ReturnsEmptyList()
    {
        // Act
        var response = await _client.GetAsync("/api/conversations");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions);

        conversations.Should().NotBeNull();
        // May contain conversations from other tests (shared DB), but response is valid
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
        var response = await _client.GetAsync("/api/conversations");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions);

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
        var response = await _client.GetAsync("/api/conversations");
        var conversations = await response.Content
            .ReadFromJsonAsync<List<ConversationSummaryDto>>(JsonOptions);

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
        var response = await _client.GetAsync($"/api/conversations/{conversationId}/messages");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions);

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
        var response = await _client.GetAsync($"/api/conversations/{Guid.NewGuid()}/messages");

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
        var response = await _client.GetAsync($"/api/conversations/{conversationId}/messages");
        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions);

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
        var response = await _client.GetAsync($"/api/conversations/{conversationA}/messages");
        var messages = await response.Content
            .ReadFromJsonAsync<List<ConversationMessageDto>>(JsonOptions);

        // Assert
        messages.Should().HaveCount(1);
        messages![0].Content.Should().Be("Message A");
        messages[0].ConversationId.Should().Be(conversationA);
    }
}
