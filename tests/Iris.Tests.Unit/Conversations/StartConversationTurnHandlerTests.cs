using FluentAssertions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.StartConversationTurn;
using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class StartConversationTurnHandlerTests
{
    private readonly IConversationQueries _conversationQueries = Substitute.For<IConversationQueries>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();
    private readonly IConversationTurnRequestStore _turnRequestStore = Substitute.For<IConversationTurnRequestStore>();
    private readonly ITurnDoorbell _doorbell = Substitute.For<ITurnDoorbell>();

    private StartConversationTurnHandler CreateSut()
    {
        _eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>(Array.Empty<RecordedEvent>()));

        _eventRecorder.ClearReceivedCalls();
        _turnRequestStore.ClearReceivedCalls();
        _doorbell.ClearReceivedCalls();

        return new StartConversationTurnHandler(_conversationQueries, _eventRecorder, _turnRequestStore, _doorbell);
    }

    private static StartConversationTurnCommand CreateValidCommand(
         Guid? userId = null,
         Guid? conversationId = null,
         string userMessage = "Hello!",
         string model = "test/model",
         bool changeModel = false,
         ModelParameters? modelParameters = null) =>
         new()
         {
             UserId = userId ?? Guid.NewGuid(),
             ConversationId = conversationId ?? Guid.NewGuid(),
             UserMessage = userMessage,
             Model = model,
             ChangeModel = changeModel,
             ModelParameters = modelParameters
         };

    private void SetupExistingConversation(Guid conversationId, bool existsForCurrentUser = true)
    {
        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(existsForCurrentUser);
    }

    [Fact]
    public async Task Handle_ValidCommand_RecordsUserMessageAndEnqueuesTurn()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var modelParameters = new ModelParameters(0.7f, 500, 0.9f);
        var command = CreateValidCommand(
            userId: userId,
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

        _turnRequestStore.Received(1).AddPending(
            Arg.Is<ConversationTurnRequest>(request =>
                request.ConversationId == conversationId &&
                request.UserId == userId &&
                request.Model == command.Model &&
                request.ChangeModel == command.ChangeModel &&
                request.Status == ConversationTurnStatus.Pending &&
                request.AttemptCount == 0 &&
                request.ModelParameters != null));

        _doorbell.Received(1).Ring();
    }

    [Fact]
    public async Task Handle_ValidCommand_AddsPendingBeforeRecordingEvent()
    {
        // Arrange — the atomic-enqueue invariant depends on AddPending happening
        // before RecordAsync (the shared DbContext's SaveChangesAsync commits both).
        var conversationId = Guid.NewGuid();
        SetupExistingConversation(conversationId);
        var sut = CreateSut();
        var command = CreateValidCommand(conversationId: conversationId);

        var callOrder = new List<string>();
        _turnRequestStore
            .When(s => s.AddPending(Arg.Any<ConversationTurnRequest>()))
            .Do(_ => callOrder.Add("AddPending"));
        _eventRecorder
            .When(r => r.RecordAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<ConversationEvent>>(), Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("RecordAsync"));

        // Act
        await sut.Handle(command, CancellationToken.None);

        // Assert
        callOrder.Should().Equal("AddPending", "RecordAsync");
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

        _turnRequestStore.DidNotReceive().AddPending(Arg.Any<ConversationTurnRequest>());
        _doorbell.DidNotReceive().Ring();
    }

    [Fact]
    public async Task Handle_ConversationDoesNotExist_ThrowsNotFoundAndDoesNotEnqueue()
    {
        // Arrange
        var command = CreateValidCommand();
        SetupExistingConversation(command.ConversationId, existsForCurrentUser: false);
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

        _turnRequestStore.DidNotReceive().AddPending(Arg.Any<ConversationTurnRequest>());
        _doorbell.DidNotReceive().Ring();
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

        _turnRequestStore.DidNotReceive().AddPending(Arg.Any<ConversationTurnRequest>());
        _doorbell.DidNotReceive().Ring();
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

        _turnRequestStore.DidNotReceive().AddPending(Arg.Any<ConversationTurnRequest>());
        _doorbell.DidNotReceive().Ring();
    }

    [Fact]
    public async Task Handle_ConversationBelongsToAnotherUser_ThrowsNotFoundAndDoesNotRecord()
    {
        // Arrange — the read model's ExistsForUserAsync is scoped by the EF query
        // filter to the caller's ICurrentUserService; simulate "belongs to another
        // user" by having it return false, the same result an attacker would get.
        var conversationId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        SetupExistingConversation(conversationId, existsForCurrentUser: false);
        var sut = CreateSut();
        var command = CreateValidCommand(userId: attackerId, conversationId: conversationId);

        // Act
        var act = () => sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());

        _turnRequestStore.DidNotReceive().AddPending(Arg.Any<ConversationTurnRequest>());
        _doorbell.DidNotReceive().Ring();
    }
}
