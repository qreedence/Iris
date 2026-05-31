using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Events;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class CreateConversationHandlerTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();

    private CreateConversationHandler CreateSut()
    {
        _eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>(Array.Empty<RecordedEvent>()));

        _eventRecorder.ClearReceivedCalls();

        return new CreateConversationHandler(_eventStore, _eventRecorder);
    }

    private static CreateConversationCommand CreateValidCommand(
        Guid? conversationId = null,
        Guid? personaId = null,
        string title = "Test Conversation") =>
        new(
            conversationId ?? Guid.NewGuid(),
            personaId ?? Guid.NewGuid(),
            title);

    // ── §1 Happy Path ─────────────────────────────────────────────

    [Fact]
    public async Task Handle_ValidCommand_RecordsConversationCreatedEvent()
    {
        // Arrange
        var sut = CreateSut();
        var command = CreateValidCommand();

        // Act
        var result = await sut.Handle(command, CancellationToken.None);

        // Assert
        result.Should().Be(command.ConversationId);

        await _eventRecorder.Received(1).RecordAsync(
            command.ConversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.Count() == 1 &&
                events.First().GetType() == typeof(ConversationCreated) &&
                ((ConversationCreated)events.First()).PersonaId == command.PersonaId &&
                ((ConversationCreated)events.First()).Title == command.Title),
            Arg.Any<CancellationToken>());
    }

    // ── §2 Validation ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_EmptyTitle_ThrowsValidationException(string? title)
    {
        // Arrange
        var sut = CreateSut();
        var command = CreateValidCommand(title: title!);

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*title*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DefaultConversationId_ThrowsValidationException()
    {
        // Arrange
        var sut = CreateSut();
        var command = CreateValidCommand(conversationId: Guid.Empty);

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*ConversationId*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DefaultPersonaId_ThrowsValidationException()
    {
        // Arrange
        var sut = CreateSut();
        var command = CreateValidCommand(personaId: Guid.Empty);

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*PersonaId*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConversationAlreadyExists_ThrowsValidationException()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var command = CreateValidCommand(conversationId: conversationId);

        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(new List<ConversationEvent>
            {
                new ConversationCreated(conversationId, Guid.NewGuid(), "Already Exists")
            });

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*already exists*");

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
        var command = CreateValidCommand();

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            command.ConversationId,
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }
}
