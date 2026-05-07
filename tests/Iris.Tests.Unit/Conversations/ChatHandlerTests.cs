using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.Chat;
using Iris.Application.Conversations.Notifications;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using MediatR;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class ChatHandlerTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IPublisher _publisher = Substitute.For<IPublisher>();
    private readonly IChatProvider _chatProvider = Substitute.For<IChatProvider>();

    private ChatHandler CreateSut() => new(_eventStore, _publisher, _chatProvider);

    private static ChatCommand CreateValidCommand(
        Guid? conversationId = null,
        string userMessage = "Hello!",
        string model = "openai/gpt-4o-mini",
        string? systemPrompt = "You are a helpful assistant.") =>
        new(
            conversationId ?? Guid.NewGuid(),
            userMessage,
            model,
            systemPrompt);

    private void SetupExistingConversation(Guid conversationId, params ConversationEvent[] extraEvents)
    {
        var events = new List<ConversationEvent>
        {
            new ConversationCreated(conversationId, Guid.NewGuid(), "Test Chat")
        };
        events.AddRange(extraEvents);

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(events);
    }

    private void SetupChatProviderResponse(string content = "Hello back!", int inputTokens = 10, int outputTokens = 5)
    {
        _chatProvider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(content, new UsageInfo(inputTokens, outputTokens, inputTokens + outputTokens)));
    }

    // ── §1 History Loading ────────────────────────────────────────

    [Fact]
    public async Task Handle_ValidRequest_LoadsConversationHistory()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);
        SetupExistingConversation(conversationId);
        SetupChatProviderResponse();

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventStore.Received(1).LoadStreamAsync(conversationId, Arg.Any<CancellationToken>());
    }

    // ── §2 ChatRequest Construction ───────────────────────────────

    [Fact]
    public async Task Handle_WithHistory_BuildsCorrectChatRequest()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);

        SetupExistingConversation(conversationId,
            new MessageSent(Guid.NewGuid(), conversationId, "First user message", ChatRole.User),
            new AssistantResponseCompleted(Guid.NewGuid(), conversationId, "First AI response", "test/model"),
            new MessageSent(Guid.NewGuid(), conversationId, "Second user message", ChatRole.User));

        SetupChatProviderResponse();

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert — verify ChatRequest sent to provider has messages in order
        await _chatProvider.Received(1).CompleteAsync(
            Arg.Is<ChatRequest>(r =>
                r.Messages.Count == 3 &&
                r.Messages[0].Role == ChatRole.User &&
                r.Messages[0].Content == "First user message" &&
                r.Messages[1].Role == ChatRole.Assistant &&
                r.Messages[1].Content == "First AI response" &&
                r.Messages[2].Role == ChatRole.User &&
                r.Messages[2].Content == "Second user message"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_IncludesSystemPrompt()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId, systemPrompt: "You are Iris.");
        SetupExistingConversation(conversationId);
        SetupChatProviderResponse();

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _chatProvider.Received(1).CompleteAsync(
            Arg.Is<ChatRequest>(r => r.SystemPrompt == "You are Iris."),
            Arg.Any<CancellationToken>());
    }

    // ── §3 Event Emission ─────────────────────────────────────────

    [Fact]
    public async Task Handle_AfterAiResponse_EmitsAssistantResponseCompleted()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId, model: "test/model");
        SetupExistingConversation(conversationId);
        SetupChatProviderResponse("AI says hello");

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _publisher.Received(1).Publish(
            Arg.Is<EventNotification<AssistantResponseCompleted>>(n =>
                n.Event.ConversationId == conversationId &&
                n.Event.Content == "AI says hello" &&
                n.Event.Model == "test/model"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AfterAiResponse_EmitsTurnCompleted()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);
        SetupExistingConversation(conversationId);
        SetupChatProviderResponse(inputTokens: 150, outputTokens: 42);

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _publisher.Received(1).Publish(
            Arg.Is<EventNotification<TurnCompleted>>(n =>
                n.Event.ConversationId == conversationId &&
                n.Event.InputTokens == 150 &&
                n.Event.OutputTokens == 42),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NullUsageInfo_EmitsTurnCompletedWithZeros()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);
        SetupExistingConversation(conversationId);

        _chatProvider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Response", null));

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _publisher.Received(1).Publish(
            Arg.Is<EventNotification<TurnCompleted>>(n =>
                n.Event.InputTokens == 0 &&
                n.Event.OutputTokens == 0),
            Arg.Any<CancellationToken>());
    }

    // ── §4 Error Handling ─────────────────────────────────────────

    [Fact]
    public async Task Handle_AiProviderFails_DoesNotEmitEvents()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);
        SetupExistingConversation(conversationId);

        _chatProvider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new Exception("AI provider down"));

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>();

        await _eventStore.DidNotReceive().AppendAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());

        await _publisher.DidNotReceive().Publish(
            Arg.Any<INotification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConversationDoesNotExist_ThrowsNotFoundException()
    {
        // Arrange
        var sut = CreateSut();
        var command = CreateValidCommand();

        _eventStore.LoadStreamAsync(command.ConversationId, Arg.Any<CancellationToken>())
            .Returns(new List<ConversationEvent>());

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();

        await _chatProvider.DidNotReceive().CompleteAsync(
            Arg.Any<ChatRequest>(),
            Arg.Any<CancellationToken>());
    }
}
