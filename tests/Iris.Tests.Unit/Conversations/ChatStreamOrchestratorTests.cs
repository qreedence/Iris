using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class ChatStreamOrchestratorTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IChatProvider _chatProvider = Substitute.For<IChatProvider>();
    private readonly IChatStreamNotifier _notifier = Substitute.For<IChatStreamNotifier>();
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();

    private ChatStreamOrchestrator CreateSut()
    {
        _eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>(Array.Empty<RecordedEvent>()));

        _eventRecorder.ClearReceivedCalls();

        return new(_eventStore, _chatProvider, _notifier, _personaService, _eventRecorder, NullLogger<ChatStreamOrchestrator>.Instance);
    }

    [Fact]
    public async Task StreamAsync_Success_StreamsChunksAndAppendsCompletionEvents()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "First user message", ChatRole.User),
                new AssistantResponseCompleted(Guid.NewGuid(), conversationId, "First assistant response", "test/model"),
                new MessageSent(Guid.NewGuid(), conversationId, "Second user message", ChatRole.User)
            ]);
        SetupPersona(personaId, systemPrompt: "You are Iris.");

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk("Hello", false, null),
                new StreamedChunk(" there", false, null),
                new StreamedChunk(null, true, new UsageInfo(12, 5, 17))
            ], call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "test/model", null, TestContext.Current.CancellationToken);

        // Assert
        _chatProvider.Received(1).StreamAsync(
            Arg.Is<ChatRequest>(request =>
                request.Model == "test/model" &&
                request.SystemPrompt == "You are Iris." &&
                request.Messages.Count == 3 &&
                request.Messages[0] == new ChatMessage(ChatRole.User, "First user message") &&
                request.Messages[1] == new ChatMessage(ChatRole.Assistant, "First assistant response") &&
                request.Messages[2] == new ChatMessage(ChatRole.User, "Second user message")),
            Arg.Any<CancellationToken>());

        await _notifier.Received(1).SendChunkAsync(conversationId, "Hello", Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendChunkAsync(conversationId, " there", Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());

        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsCompletionEvents(events)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ProviderFailure_AppendsTurnFailedAndSendsError()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new ChatRateLimitException("Raw provider message."),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "test/model", null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsRateLimitFailure(events)),
            Arg.Any<CancellationToken>());

        await _notifier.Received(1).SendChunkAsync(conversationId, "partial", Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendErrorAsync(
            conversationId,
            "rate_limited",
            "The AI provider rate limit was exceeded.",
            Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_Cancellation_AppendsTurnCancelledWithPartialContent()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new OperationCanceledException(),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "test/model", null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsCancellation(events)),
            CancellationToken.None);

        await _notifier.Received(1).SendChunkAsync(conversationId, "partial", Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().SendErrorAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_UnexpectedException_AppendsTurnFailedAndDoesNotEmitSuccessEvents()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new InvalidOperationException("Something completely unexpected"),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "test/model", null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsInternalFailure(events)),
            CancellationToken.None);

        await _notifier.Received(1).SendChunkAsync(conversationId, "partial", Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendErrorAsync(
            conversationId,
            "internal_error",
            "An unexpected error occurred.",
            CancellationToken.None);
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<AssistantResponseCompleted>().Any() ||
                events.OfType<TurnCompleted>().Any()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_PersonaHasSystemPrompt_PassesToProvider()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId, systemPrompt: "Speak plainly.");
        ChatRequest? capturedRequest = null;

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "fallback/model", null, TestContext.Current.CancellationToken);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.SystemPrompt.Should().Be("Speak plainly.");
    }

    [Fact]
    public async Task StreamAsync_PersonaHasNoSystemPrompt_SendsNullToProvider()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId, systemPrompt: null);
        ChatRequest? capturedRequest = null;

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "fallback/model", null, TestContext.Current.CancellationToken);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_PersonaHasModelPreference_UsesPreferenceWhenRequestMatches()
    {
        // Arrange — request sends same model as preference (no switch)
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId, modelPreference: "persona/model");
        ChatRequest? capturedRequest = null;

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        // Act — frontend sends persona's preferred model (user hasn't switched)
        await sut.StreamAsync(conversationId, "persona/model", null, TestContext.Current.CancellationToken);

        // Assert — preference used, no ModelChanged emitted
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Model.Should().Be("persona/model");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ConversationEvent>>(events => events.OfType<ModelChanged>().Any()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_PersonaHasNoModelPreference_UsesFallbackModel()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId, modelPreference: null);
        ChatRequest? capturedRequest = null;

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "fallback/model", null, TestContext.Current.CancellationToken);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Model.Should().Be("fallback/model");
    }

    [Fact]
    public async Task StreamAsync_PersonaNotFound_AppendsTurnFailed()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User)
            ]);
        _personaService.GetForConversationAsync(personaId, Arg.Any<CancellationToken>())
            .Returns<Task<PersonaDto>>(_ => throw new NotFoundException("Persona not found."));

        // Act
        await sut.StreamAsync(conversationId, "fallback/model", null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsPersonaNotFoundFailure(events)),
            CancellationToken.None);

        await _notifier.Received(1).SendErrorAsync(
            conversationId,
            "persona_not_found",
            "The persona for this conversation no longer exists.",
            CancellationToken.None);
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
        _chatProvider.DidNotReceive().StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ModelDiffersFromEffective_EmitsModelChanged()
    {
        // Arrange — persona prefers "persona/model", request sends "new/model"
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId, modelPreference: "persona/model");
        ChatRequest? capturedRequest = null;

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "new/model", null, TestContext.Current.CancellationToken);

        // Assert — ModelChanged event emitted
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<ModelChanged>().Any(e => e.Model == "new/model")),
            Arg.Any<CancellationToken>());

        // Streaming uses the new model
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Model.Should().Be("new/model");
    }

    [Fact]
    public async Task StreamAsync_ModelMatchesEffective_NoModelChangedEmitted()
    {
        // Arrange — persona prefers "persona/model", request sends same
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId, modelPreference: "persona/model");

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                _ => { },
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(conversationId, "persona/model", null, TestContext.Current.CancellationToken);

        // Assert — no ModelChanged emitted
        await _eventRecorder.DidNotReceive().RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<ModelChanged>().Any()),
            Arg.Any<CancellationToken>());

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ConversationEvent>>(events => events.OfType<ModelChanged>().Any()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_HasModelChangedEvent_UsesModelChangedOverEverything()
    {
        // Arrange — ModelChanged says "changed/model", persona prefers "persona/model", request says "changed/model"
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User),
                new ModelChanged(conversationId, "changed/model")
            ]);
        SetupPersona(personaId, modelPreference: "persona/model");
        ChatRequest? capturedRequest = null;

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        // Act — request also sends "changed/model" (matches, no new event)
        await sut.StreamAsync(conversationId, "changed/model", null, TestContext.Current.CancellationToken);

        // Assert — uses ModelChanged model, NOT persona preference
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Model.Should().Be("changed/model");

        // No new ModelChanged emitted (request matches)
        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ConversationEvent>>(events => events.OfType<ModelChanged>().Any()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ModelChangedThenSwitchAgain_EmitsNewModelChanged()
    {
        // Arrange — previous ModelChanged to "old/model", now switching to "newer/model"
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User),
                new ModelChanged(conversationId, "old/model")
            ]);
        SetupPersona(personaId, modelPreference: "persona/model");
        ChatRequest? capturedRequest = null;

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CaptureAndStreamResponse(
                call.Arg<ChatRequest>(),
                request => capturedRequest = request,
                call.ArgAt<CancellationToken>(1)));

        // Act — request sends "newer/model" (differs from "old/model")
        await sut.StreamAsync(conversationId, "newer/model", null, TestContext.Current.CancellationToken);

        // Assert — emits new ModelChanged, uses newer model
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<ModelChanged>().Any(e => e.Model == "newer/model")),
            Arg.Any<CancellationToken>());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Model.Should().Be("newer/model");
    }

    private void SetupExistingConversation(
        Guid conversationId,
        string? systemPrompt = null,
        string? modelPreference = null)
    {
        var personaId = Guid.NewGuid();
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User)
            ]);
        SetupPersona(personaId, systemPrompt, modelPreference);
    }

    private void SetupPersona(Guid personaId, string? systemPrompt = null, string? modelPreference = null)
    {
        _personaService.GetForConversationAsync(personaId, Arg.Any<CancellationToken>())
            .Returns(new PersonaDto(
                personaId,
                "Iris",
                systemPrompt,
                modelPreference,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
    }

    private static bool ContainsCompletionEvents(IEnumerable<ConversationEvent> events)
    {
        var eventList = events.ToList();
        var response = eventList.OfType<AssistantResponseCompleted>().SingleOrDefault();
        var turn = eventList.OfType<TurnCompleted>().SingleOrDefault();

        return response?.Content == "Hello there" &&
            response.Model == "test/model" &&
            turn?.InputTokens == 12 &&
            turn.OutputTokens == 5 &&
            eventList.All(e => e is not MessageSent);
    }

    private static bool ContainsRateLimitFailure(IEnumerable<ConversationEvent> events)
    {
        var failed = events.SingleOrDefault() as TurnFailed;

        return failed?.Source == FailureSource.Provider &&
            failed.ErrorCode == "rate_limited" &&
            failed.Message == "The AI provider rate limit was exceeded." &&
            failed.PartialContent == "partial";
    }

    private static bool ContainsInternalFailure(IEnumerable<ConversationEvent> events)
    {
        var failed = events.SingleOrDefault() as TurnFailed;

        return failed?.Source == FailureSource.Internal &&
            failed.ErrorCode == "internal_error" &&
            failed.Message == "An unexpected error occurred." &&
            failed.PartialContent == "partial";
    }

    private static bool ContainsCancellation(IEnumerable<ConversationEvent> events)
    {
        var cancelled = events.SingleOrDefault() as TurnCancelled;
        return cancelled?.PartialContent == "partial";
    }

    private static bool ContainsPersonaNotFoundFailure(IEnumerable<ConversationEvent> events)
    {
        var failed = events.SingleOrDefault() as TurnFailed;

        return failed?.Source == FailureSource.Internal &&
            failed.ErrorCode == "persona_not_found" &&
            failed.Message == "The persona for this conversation no longer exists." &&
            failed.PartialContent is null;
    }

    private static async IAsyncEnumerable<StreamedChunk> CaptureAndStreamResponse(
        ChatRequest request,
        Action<ChatRequest> capture,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        capture(request);
        await foreach (var chunk in StreamChunks([
            new StreamedChunk("response", false, null),
            new StreamedChunk(null, true, new UsageInfo(1, 1, 2))
        ], ct))
        {
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<StreamedChunk> StreamChunks(
        IEnumerable<StreamedChunk> chunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<StreamedChunk> StreamThenThrow(
        Exception exception,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk("partial", false, null);
        await Task.Yield();
        throw exception;
    }
}
