using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Notifications;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class ChatStreamOrchestratorTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IChatProvider _chatProvider = Substitute.For<IChatProvider>();
    private readonly IChatStreamNotifier _notifier = Substitute.For<IChatStreamNotifier>();
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();
    private readonly IPublisher _publisher = Substitute.For<IPublisher>();

    private ChatStreamOrchestrator CreateSut() =>
        new(_eventStore, _chatProvider, _notifier, _personaService, _publisher, NullLogger<ChatStreamOrchestrator>.Instance);

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

        await _eventStore.Received(1).AppendAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsCompletionEvents(events)),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());

        await _publisher.Received(1).Publish(
            Arg.Is<EventNotification<AssistantResponseCompleted>>(n => n.Event.Content == "Hello there"),
            Arg.Any<CancellationToken>());
        await _publisher.Received(1).Publish(
            Arg.Is<EventNotification<TurnCompleted>>(n => n.Event.InputTokens == 12 && n.Event.OutputTokens == 5),
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
        await _eventStore.Received(1).AppendAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsRateLimitFailure(events)),
            Arg.Any<Guid>(),
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
        await _eventStore.Received(1).AppendAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsCancellation(events)),
            Arg.Any<Guid>(),
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
        await _eventStore.Received(1).AppendAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsInternalFailure(events)),
            Arg.Any<Guid>(),
            CancellationToken.None);

        await _notifier.Received(1).SendChunkAsync(conversationId, "partial", Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendErrorAsync(
            conversationId,
            "internal_error",
            "An unexpected error occurred.",
            CancellationToken.None);
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());

        await _publisher.DidNotReceive().Publish(
            Arg.Any<EventNotification<AssistantResponseCompleted>>(),
            Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().Publish(
            Arg.Any<EventNotification<TurnCompleted>>(),
            Arg.Any<CancellationToken>());
    }

    private void SetupExistingConversation(Guid conversationId)
    {
        var personaId = Guid.NewGuid();
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns([
                new ConversationCreated(conversationId, personaId, "Chat"),
                new MessageSent(Guid.NewGuid(), conversationId, "Hello", ChatRole.User)
            ]);
        SetupPersona(personaId);
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
