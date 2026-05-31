using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Queries;
using Iris.Application.Personas;
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

    private async Task<PersonaDto> CreatePersonaAsync(
        string name = "Iris",
        string? systemPrompt = null,
        string? modelPreference = null)
    {
        using var scope = _factory.Services.CreateScope();
        var personaService = scope.ServiceProvider.GetRequiredService<IPersonaService>();
        return await personaService.CreateAsync(
            new CreatePersonaRequest(Guid.NewGuid(), name, systemPrompt, modelPreference),
            TestContext.Current.CancellationToken);
    }

    private static ChatRequestDto CreateChatRequest(
        string userMessage = "Hello!",
        string model = "test/model",
        bool changeModel = false) =>
        new(userMessage, model, changeModel);

    [Fact]
    public async Task PostChat_ValidConversation_Returns202Accepted()
    {
        // Arrange
        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

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
        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

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

        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

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

    [Fact]
    public async Task PostChat_PersonaWithSystemPrompt_ProviderReceivesPrompt()
    {
        // Arrange
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        var userMessage = $"prompt-test-{Guid.NewGuid()}";
        var persona = await CreatePersonaAsync(systemPrompt: "Answer as Iris.");
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.Content == userMessage);
        capturedRequest!.SystemPrompt.Should().Be("Answer as Iris.");
    }

    [Fact]
    public async Task PostChat_PersonaWithModelPreference_ProviderUsesPreferenceWhenRequestMatches()
    {
        // Arrange — frontend sends the persona's preferred model (user hasn't switched)
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        var userMessage = $"model-preference-test-{Guid.NewGuid()}";
        var persona = await CreatePersonaAsync(modelPreference: "persona/model");
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

        // Act — request model matches persona preference
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "persona/model"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.Content == userMessage);
        capturedRequest!.Model.Should().Be("persona/model");
    }

    [Fact]
    public async Task PostChat_PersonaWithModelPreferenceAndChangeModelFalse_ProviderUsesPreferenceWhenRequestDiffers()
    {
        // Arrange — frontend sends fallback model, but does not request a model change.
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        var userMessage = $"model-preference-fallback-test-{Guid.NewGuid()}";
        var persona = await CreatePersonaAsync(modelPreference: "persona/model");
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "frontend/fallback", changeModel: false),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.Content == userMessage);
        capturedRequest!.Model.Should().Be("persona/model");
    }

    [Fact]
    public async Task PostChat_ChangeModelTrue_ProviderUsesRequestedModel()
    {
        // Arrange
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        var userMessage = $"model-change-test-{Guid.NewGuid()}";
        var persona = await CreatePersonaAsync(modelPreference: "persona/model");
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "new/model", changeModel: true),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.Content == userMessage);
        capturedRequest!.Model.Should().Be("new/model");
    }

    [Fact]
    public async Task PostChat_PersonaWithoutModelPreference_ProviderUsesFallbackModel()
    {
        // Arrange
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        var userMessage = $"fallback-model-test-{Guid.NewGuid()}";
        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "fallback/model"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.Content == userMessage);
        capturedRequest!.Model.Should().Be("fallback/model");
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
