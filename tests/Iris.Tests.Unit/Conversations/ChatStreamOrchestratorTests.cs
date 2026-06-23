using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class ChatStreamOrchestratorTests
{
    private readonly IConversationTurnPreparer _turnPreparer = Substitute.For<IConversationTurnPreparer>();
    private readonly IChatProvider _chatProvider = Substitute.For<IChatProvider>();
    private readonly IChatStreamNotifier _notifier = Substitute.For<IChatStreamNotifier>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();

    private ChatStreamOrchestrator CreateSut()
    {
        _eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>(Array.Empty<RecordedEvent>()));

        _eventRecorder.ClearReceivedCalls();

        return new(
            _turnPreparer,
            _chatProvider,
            _notifier,
            _eventRecorder,
            NullLogger<ChatStreamOrchestrator>.Instance);
    }

    [Fact]
    public async Task StreamAsync_Success_StreamsChunksAndRecordsCompletionEvents()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var chatRequest = new ChatRequest(
            "test/model",
            [
                new ChatMessage(ChatRole.User, "First user message"),
                new ChatMessage(ChatRole.Assistant, "First assistant response"),
                new ChatMessage(ChatRole.User, "Second user message")
            ],
            "You are Iris.",
            null);
        SetupPreparedTurn(conversationId, chatRequest);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk("Hello", false, null),
                new StreamedChunk(" there", false, null),
                new StreamedChunk(null, true, new UsageInfo(12, 5, 17))
            ], call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId,"test/model", false, null, TestContext.Current.CancellationToken);

        // Assert
        _chatProvider.Received(1).StreamAsync(chatRequest, Arg.Any<CancellationToken>());

        await _notifier.Received(1).SendChunkAsync(conversationId, "Hello", Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendChunkAsync(conversationId, " there", Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());

        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsCompletionEvents(events)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_PreStreamEvents_RecordsBeforeStreaming()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var modelChanged = new ModelChanged(conversationId, "new/model");
        var chatRequest = CreateChatRequest("new/model");
        SetupPreparedTurn(conversationId, chatRequest, [modelChanged]);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk("response", false, null),
                new StreamedChunk(null, true, new UsageInfo(1, 1, 2))
            ], call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId,"new/model", true, null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => events.Single() == modelChanged),
            Arg.Any<CancellationToken>());

        _chatProvider.Received(1).StreamAsync(chatRequest, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ProviderFailure_RecordsTurnFailedAndSendsError()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new ChatRateLimitException("Raw provider message."),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId,"test/model", false, null, TestContext.Current.CancellationToken);

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
    public async Task StreamAsync_Cancellation_RecordsTurnCancelledWithPartialContent()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new OperationCanceledException(),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId,"test/model", false, null, TestContext.Current.CancellationToken);

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
    public async Task StreamAsync_UnexpectedException_RecordsTurnFailedAndDoesNotEmitSuccessEvents()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new InvalidOperationException("Something completely unexpected"),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId,"test/model", false, null, TestContext.Current.CancellationToken);

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
    public async Task StreamAsync_PersonaNotFound_RecordsTurnFailedAndDoesNotStream()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();

        _turnPreparer.PrepareAsync(Arg.Any<Guid>(), conversationId, Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<ModelParameters?>(), Arg.Any<CancellationToken>())
            .Returns<Task<PreparedConversationTurn>>(_ => throw new ConversationPersonaNotFoundException(conversationId, personaId));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId,"fallback/model", false, null, TestContext.Current.CancellationToken);

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

    private void SetupPreparedTurn(
        Guid conversationId,
        ChatRequest? chatRequest = null,
        IReadOnlyList<ConversationEvent>? preStreamEvents = null)
    {
        _turnPreparer.PrepareAsync(Arg.Any<Guid>(), conversationId, Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<ModelParameters?>(), Arg.Any<CancellationToken>())
            .Returns(new PreparedConversationTurn(
                chatRequest ?? CreateChatRequest(),
                preStreamEvents ?? []));
    }

    private static ChatRequest CreateChatRequest(string model = "test/model") =>
        new(
            model,
            [new ChatMessage(ChatRole.User, "Hello")],
            null,
            null);

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
