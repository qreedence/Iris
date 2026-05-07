using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations.Commands.Chat;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Commands.SendMessage;
using Iris.Domain.AiIntegration;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Iris.Tests.Integration.Conversations;

public class ChatEndpointTests : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public ChatEndpointTests(ApiTestFactory factory)
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

    private static ChatRequestDto CreateChatRequest(
        string userMessage = "Hello!",
        string model = "test/model",
        string? systemPrompt = null) =>
        new(userMessage, model, systemPrompt);

    // ── §1 Happy path ─────────────────────────────────────────────

    [Fact]
    public async Task PostChat_ValidConversation_Returns200WithResponse()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));
        await SendCommand(new SendMessageCommand(conversationId, "Hello!", ChatRole.User));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var chatResponse = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);
        chatResponse.Should().NotBeNull();
        chatResponse!.Content.Should().NotBeNullOrEmpty();
    }

    // ── §2 Non-existent conversation ──────────────────────────────

    [Fact]
    public async Task PostChat_NonExistentConversation_Returns404()
    {
        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/chat",
            CreateChatRequest());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── §3 Response shape ─────────────────────────────────────────

    [Fact]
    public async Task PostChat_ResponseShape_MatchesExpectedDto()
    {
        // Arrange
        _factory.MockChatProvider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Shaped response", new UsageInfo(100, 50, 150)));

        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));
        await SendCommand(new SendMessageCommand(conversationId, "Hello!", ChatRole.User));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest());

        var chatResponse = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);

        // Assert
        chatResponse.Should().NotBeNull();
        chatResponse!.Content.Should().Be("Shaped response");
        chatResponse.UsageInfo.Should().NotBeNull();
        chatResponse.UsageInfo!.InputTokens.Should().Be(100);
        chatResponse.UsageInfo.OutputTokens.Should().Be(50);
        chatResponse.UsageInfo.TotalTokens.Should().Be(150);
    }

    // ── §4 Multi-turn via API ─────────────────────────────────────

    [Fact]
    public async Task PostChat_MultiTurn_AiReceivesHistory()
    {
        // Arrange — capture the request on the second chat call
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("First AI reply", new UsageInfo(10, 5, 15)),
                     new ChatResponse("Second AI reply", new UsageInfo(20, 10, 30)))
            .AndDoes(info => capturedRequest = info.Arg<ChatRequest>());

        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Turn 1
        await SendCommand(new SendMessageCommand(conversationId, "First question", ChatRole.User));
        await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest("First question"));

        // Turn 2
        await SendCommand(new SendMessageCommand(conversationId, "Follow-up", ChatRole.User));

        // Act
        await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest("Follow-up"));

        // Assert — second call should have full history
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Messages.Should().HaveCount(3);
        capturedRequest.Messages[0].Content.Should().Be("First question");
        capturedRequest.Messages[1].Content.Should().Be("First AI reply");
        capturedRequest.Messages[2].Content.Should().Be("Follow-up");
    }
}
