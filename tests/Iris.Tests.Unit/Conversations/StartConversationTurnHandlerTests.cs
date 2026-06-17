using FluentAssertions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.StartConversationTurn;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class StartConversationTurnHandlerTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();
    private readonly IConversationTurnQueue _turnQueue = Substitute.For<IConversationTurnQueue>();

    private StartConversationTurnHandler CreateSut()
    {
        _eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>(Array.Empty<RecordedEvent>()));

        _turnQueue.EnqueueAsync(
                Arg.Any<ConversationTurnWorkItem>(),
                Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        _eventRecorder.ClearReceivedCalls();
        _turnQueue.ClearReceivedCalls();

        return new StartConversationTurnHandler(_eventStore, _eventRecorder, _turnQueue);
    }

    private static StartConversationTurnCommand CreateValidCommand(
        Guid? conversationId = null,
        string userMessage = "Hello!",
        string model = "test/model",
        bool changeModel = false,
        ModelParameters? modelParameters = null) =>
        new(
            conversationId ?? Guid.NewGuid(),
            userMessage,
            model,
            changeModel,
            modelParameters);

    private void SetupExistingConversation(Guid conversationId)
    {
        _eventStore.LoadStreamAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(new List<ConversationEvent>
            {
                new ConversationCreated(conversationId, Guid.NewGuid(), Guid.NewGuid(), "Existing Conversation")
            });
    }

    [Fact]
    public async Task Handle_ValidCommand_RecordsUserMessageAndEnqueuesTurn()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var modelParameters = new ModelParameters(0.7f, 500, 0.9f);
        var command = CreateValidCommand(
            conversationId: conversationId,
            userMessage: "Hello from the user",
            model: "test/model",
            changeModel: true,
            modelParameters: modelParameters);
        SetupExistingConversation(conversationId);
        var sut = CreateSut();

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.Count() == 1 &&
                events.First().GetType() == typeof(MessageSent) &&
                ((MessageSent)events.First()).ConversationId == conversationId &&
                ((MessageSent)events.First()).Content == command.UserMessage &&
                ((MessageSent)events.First()).Role == ChatRole.User),
            Arg.Any<CancellationToken>());

        await _turnQueue.Received(1).EnqueueAsync(
            Arg.Is<ConversationTurnWorkItem>(workItem =>
                workItem.ConversationId == conversationId &&
                workItem.Model == command.Model &&
                workItem.ChangeModel == command.ChangeModel &&
                workItem.ModelParameters == modelParameters),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_EmptyModel_ThrowsValidationException(string? model)
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId);
        var sut = CreateSut();
        var command = CreateValidCommand(conversationId: conversationId, model: model!);

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*Model*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());

        await _turnQueue.DidNotReceive().EnqueueAsync(
            Arg.Any<ConversationTurnWorkItem>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConversationDoesNotExist_ThrowsNotFoundAndDoesNotEnqueue()
    {
        // Arrange
        var command = CreateValidCommand();
        _eventStore.LoadStreamAsync(command.ConversationId, Arg.Any<CancellationToken>())
            .Returns(new List<ConversationEvent>());
        var sut = CreateSut();

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*does not exist*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());

        await _turnQueue.DidNotReceive().EnqueueAsync(
            Arg.Any<ConversationTurnWorkItem>(),
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

        await _turnQueue.DidNotReceive().EnqueueAsync(
            Arg.Any<ConversationTurnWorkItem>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_EmptyUserMessage_ThrowsValidationException(string? userMessage)
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId);
        var sut = CreateSut();
        var command = CreateValidCommand(conversationId: conversationId, userMessage: userMessage!);

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*Content*");

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());

        await _turnQueue.DidNotReceive().EnqueueAsync(
            Arg.Any<ConversationTurnWorkItem>(),
            Arg.Any<CancellationToken>());
    }
}
