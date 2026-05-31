using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.SendMessage;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class SendMessageHandlerTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();

    private SendMessageHandler CreateSut()
    {
        _eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>(Array.Empty<RecordedEvent>()));

        _eventRecorder.ClearReceivedCalls();

        return new SendMessageHandler(_eventStore, _eventRecorder);
    }

    private static SendMessageCommand CreateValidCommand(
        Guid? conversationId = null,
        string content = "Hello, world!",
        ChatRole role = ChatRole.User) =>
        new(
            conversationId ?? Guid.NewGuid(),
            content,
            role);

    /// <summary>
    /// Sets up the mock so LoadStreamAsync returns a non-empty stream,
    /// simulating an existing conversation.
    /// </summary>
    private void SetupExistingConversation(Guid conversationId)
    {
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(new List<ConversationEvent>
            {
                new ConversationCreated(conversationId, Guid.NewGuid(), "Existing Conversation")
            });
    }

    // ── §1 Happy Path ─────────────────────────────────────────────

    [Fact]
    public async Task Handle_ValidCommand_RecordsMessageSentEvent()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);
        SetupExistingConversation(conversationId);

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.Count() == 1 &&
                events.First().GetType() == typeof(MessageSent) &&
                ((MessageSent)events.First()).ConversationId == conversationId &&
                ((MessageSent)events.First()).Content == command.Content &&
                ((MessageSent)events.First()).Role == command.Role),
            Arg.Any<CancellationToken>());
    }

    // ── §2 Validation ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_EmptyContent_ThrowsValidationException(string? content)
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId, content: content!);
        SetupExistingConversation(conversationId);

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*Content*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConversationDoesNotExist_ThrowsNotFoundException()
    {
        // Arrange
        var sut = CreateSut();
        var command = CreateValidCommand();

        // LoadStreamAsync returns empty list by default (NSubstitute default) — no setup needed
        _eventStore.LoadStreamAsync(command.ConversationId, Arg.Any<CancellationToken>())
            .Returns(new List<ConversationEvent>());

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*does not exist*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }

    // ── §3 Event Recorder Interaction ───────────────────────────

    [Fact]
    public async Task Handle_ValidCommand_PassesCorrectAggregateIdToRecorder()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);
        SetupExistingConversation(conversationId);

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }

    // ── §4 Role Passthrough ───────────────────────────────────────

    [Theory]
    [InlineData(ChatRole.User)]
    [InlineData(ChatRole.System)]
    public async Task Handle_GivenRole_EventHasMatchingRole(ChatRole role)
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId, role: role);
        SetupExistingConversation(conversationId);

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.First().GetType() == typeof(MessageSent) &&
                ((MessageSent)events.First()).Role == role),
            Arg.Any<CancellationToken>());
    }
}
