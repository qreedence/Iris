using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Api.Authentication;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Queries;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Tests.Integration.Helpers;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Iris.Domain.Conversations.Content;

namespace Iris.Tests.Integration.Conversations;

[Collection("ApiTestFactory collection")]
public class ChatEndpointTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public ChatEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(_userId);
    }

    private Task SendCommand<TResponse>(IRequest<TResponse> command) => SendCommandAs(_userId, command);

    private Task SendCommandAs<TResponse>(Guid userId, IRequest<TResponse> command) =>
        _factory.Services.SendCommandAsAsync(userId, command, TestContext.Current.CancellationToken);

    private Task<PersonaDto> CreatePersonaAsync(
        string name = "Iris",
        SystemPromptSectionsRequest? systemPrompt = null,
        string? modelPreference = null,
        Guid? userId = null) =>
        TestPersonas.CreateAsync(
            _factory.Services,
            userId ?? _userId,
            name,
            systemPrompt,
            modelPreference,
            TestContext.Current.CancellationToken);

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
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

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
    public async Task PostChat_OtherUsersConversation_Returns404AndDoesNotAppendEvents()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        var otherPersona = await CreatePersonaAsync(userId: otherUserId);
        var conversationId = Guid.NewGuid();
        await SendCommandAs(otherUserId, new CreateConversationCommand(conversationId, otherUserId, otherPersona.Id, "Not Mine"));

        // Count events before
        using var scopeBefore = _factory.Services.CreateScope();
        var storeBefore = scopeBefore.ServiceProvider.GetRequiredService<IEventStore>();
        var eventsBefore = await storeBefore.LoadStreamAsync(conversationId, TestContext.Current.CancellationToken);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scopeAfter = _factory.Services.CreateScope();
        var storeAfter = scopeAfter.ServiceProvider.GetRequiredService<IEventStore>();
        var eventsAfter = await storeAfter.LoadStreamAsync(conversationId, TestContext.Current.CancellationToken);

        eventsAfter.Should().HaveCount(eventsBefore.Count, "no events should be appended for another user's conversation");
    }

    [Fact]
    public async Task PostChat_ValidConversation_PersistsUserMessage()
    {
        // Arrange
        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(),
            TestContext.Current.CancellationToken);

        using var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = _userId;
        var queries = scope.ServiceProvider.GetRequiredService<IConversationQueries>();
        var messages = await queries.GetMessagesAsync(conversationId, 0, 10, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        messages.Should().NotBeNull();
        messages!.Should().ContainSingle(message => MessageContentBlocks.ToVisibleText(message.ContentBlocks) == "Hello!");
    }

    [Fact]
    public async Task PostChat_MultiTurn_AiReceivesHistory()
    {
        // Arrange — unique markers so this test's capture only fires for its own
        // requests; the shared MockChatProvider now spans every test in the
        // ApiTestFactory collection, so an unfiltered Arg.Any<ChatRequest>() override
        // would otherwise leak into concurrently/subsequently run tests.
        var marker1 = $"First question {Guid.NewGuid()}";
        var marker2 = $"Follow-up {Guid.NewGuid()}";
        ChatRequest? capturedRequest = null;
        InstallCapturingStream(request => capturedRequest = request, marker1, marker2);

        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Turn 1
        await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(marker1),
            TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.VisibleText == marker1);

        // Act - Turn 2
        await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(marker2),
            TestContext.Current.CancellationToken);

        // Assert - latest stream should include both user messages in chronological order.
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.VisibleText == marker2);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Messages[0].VisibleText.Should().Be(marker1);
        capturedRequest.Messages[^1].VisibleText.Should().Be(marker2);
    }

    [Fact]
    public async Task PostChat_PersonaWithSystemPrompt_ProviderReceivesAssembledPrompt()
    {
        // Arrange
        var userMessage = $"prompt-test-{Guid.NewGuid()}";
        ChatRequest? capturedRequest = null;
        InstallCapturingStream(request => capturedRequest = request, userMessage);

        var persona = await CreatePersonaAsync(systemPrompt: new SystemPromptSectionsRequest(
            Identity: "Answer as Iris.",
            Voice: "Warm and concise."));
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.VisibleText == userMessage);
        capturedRequest!.SystemPrompt.Should().Be(
            "<app_context>" + Environment.NewLine +
            "Test app context" + Environment.NewLine +
            "</app_context>" + Environment.NewLine + Environment.NewLine +
            "<guidelines>" + Environment.NewLine +
            "Test guidelines" + Environment.NewLine +
            "</guidelines>" + Environment.NewLine + Environment.NewLine +
            "<identity>" + Environment.NewLine +
            "Answer as Iris." + Environment.NewLine +
            "</identity>" + Environment.NewLine + Environment.NewLine +
            "<voice>" + Environment.NewLine +
            "Warm and concise." + Environment.NewLine +
            "</voice>");
    }

    [Fact]
    public async Task PostChat_PersonaWithModelPreference_ProviderUsesPreferenceWhenRequestMatches()
    {
        // Arrange — frontend sends the persona's preferred model (user hasn't switched)
        var userMessage = $"model-preference-test-{Guid.NewGuid()}";
        ChatRequest? capturedRequest = null;
        InstallCapturingStream(request => capturedRequest = request, userMessage);

        var persona = await CreatePersonaAsync(modelPreference: "persona/model");
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act — request model matches persona preference
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "persona/model"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.VisibleText == userMessage);
        capturedRequest!.Model.Should().Be("persona/model");
    }

    [Fact]
    public async Task PostChat_PersonaWithModelPreferenceAndChangeModelFalse_ProviderUsesPreferenceWhenRequestDiffers()
    {
        // Arrange — frontend sends fallback model, but does not request a model change.
        var userMessage = $"model-preference-fallback-test-{Guid.NewGuid()}";
        ChatRequest? capturedRequest = null;
        InstallCapturingStream(request => capturedRequest = request, userMessage);

        var persona = await CreatePersonaAsync(modelPreference: "persona/model");
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "frontend/fallback", changeModel: false),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.VisibleText == userMessage);
        capturedRequest!.Model.Should().Be("persona/model");
    }

    [Fact]
    public async Task PostChat_ChangeModelTrue_ProviderUsesRequestedModel()
    {
        // Arrange
        var userMessage = $"model-change-test-{Guid.NewGuid()}";
        ChatRequest? capturedRequest = null;
        InstallCapturingStream(request => capturedRequest = request, userMessage);

        var persona = await CreatePersonaAsync(modelPreference: "persona/model");
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "new/model", changeModel: true),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.VisibleText == userMessage);
        capturedRequest!.Model.Should().Be("new/model");
    }

    [Fact]
    public async Task PostChat_PersonaWithoutModelPreference_ProviderUsesFallbackModel()
    {
        // Arrange
        var userMessage = $"fallback-model-test-{Guid.NewGuid()}";
        ChatRequest? capturedRequest = null;
        InstallCapturingStream(request => capturedRequest = request, userMessage);

        var persona = await CreatePersonaAsync();
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, persona.Id, "Chat"));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            CreateChatRequest(userMessage, "fallback/model"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilAsync(() => capturedRequest?.Messages.LastOrDefault()?.VisibleText == userMessage);
        capturedRequest!.Model.Should().Be("fallback/model");
    }

    /// <summary>
    /// Installs a StreamAsync override on the shared MockChatProvider that only
    /// captures/streams for requests whose latest user message matches one of this
    /// test's own unique markers — everything else falls through to the provider's
    /// unmodified default stub. The mock is now shared across the whole
    /// ApiTestFactory collection (Phase 5), so an unmarked Arg.Any() override would
    /// otherwise leak into concurrently/subsequently run tests in other classes.
    /// </summary>
    private void InstallCapturingStream(Action<ChatRequest> capture, params string[] markers)
    {
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                var ct = call.ArgAt<CancellationToken>(1);
                if (markers.Contains(request.Messages.LastOrDefault()?.VisibleText))
                    return CaptureAndStreamResponse(request, capture, ct);
                return ChatProviderMock.DefaultStream(ct);
            });
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
