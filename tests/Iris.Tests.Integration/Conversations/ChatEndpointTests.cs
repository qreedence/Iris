using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Queries;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Iris.Tests.Integration.Conversations;

public class ChatEndpointTests : IClassFixture<ApiTestFactory>
{
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

    [Fact]
    public async Task PostChat_ValidConversation_Returns202Accepted()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PostChat_NonExistentConversation_Returns404()
    {
        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/chat",
            CreateChatRequest(),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostChat_ValidConversation_PersistsUserMessage()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(),
            TestContext.Current.CancellationToken);

        using var scope = _factory.Services.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<IConversationQueries>();
        var messages = await queries.GetMessagesAsync(conversationId, 0, 10, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        messages.Should().NotBeNull();
        messages!.Should().ContainSingle(message => message.Content == "Hello!");
    }

    [Fact]
    public async Task PostChat_MultiTurn_AiReceivesHistory()
    {
        // Arrange
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Turn 1
        await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest("First question"),
            TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.Content == "First question");

        // Act - Turn 2
        await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest("Follow-up"),
            TestContext.Current.CancellationToken);

        // Assert - latest stream should include both user messages in chronological order.
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.Content == "Follow-up");
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Messages[0].Content.Should().Be("First question");
        capturedRequest.Messages[^1].Content.Should().Be("Follow-up");
    }

    private static async IAsyncEnumerable<StreamedChunk> CaptureAndStreamResponse(
        ChatRequest request,
        Action<ChatRequest> capture,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        capture(request);
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk("AI reply", false, null);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk(null, true, new UsageInfo(10, 5, 15));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }
}
