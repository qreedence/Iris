using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CancelConversationTurn;
using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class CancelConversationTurnHandlerTests
{
    private readonly IConversationQueries _conversationQueries = Substitute.For<IConversationQueries>();
    private readonly IConversationTurnRequestStore _turnRequestStore = Substitute.For<IConversationTurnRequestStore>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();
    private readonly IActiveTurnRegistry _activeTurns = Substitute.For<IActiveTurnRegistry>();

    private CancelConversationTurnHandler CreateSut() =>
        new(_conversationQueries, _turnRequestStore, _eventRecorder, _activeTurns);

    private static ConversationTurnRequest Request(
        Guid conversationId,
        ConversationTurnStatus status,
        Guid? messageId = null,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = Guid.NewGuid(),
            MessageId = messageId ?? Guid.NewGuid(),
            Model = "test/model",
            Status = status,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task Handle_NotOwner_ThrowsNotFound()
    {
        var conversationId = Guid.NewGuid();
        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateSut();

        var act = () => sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        await _turnRequestStore.DidNotReceive().MarkCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoActiveTurn_NoOp()
    {
        var conversationId = Guid.NewGuid();
        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>()).Returns(true);
        _turnRequestStore.GetActiveAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ConversationTurnRequest>());
        var sut = CreateSut();

        await sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        await _turnRequestStore.DidNotReceive().MarkCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _activeTurns.DidNotReceive().TryCancel(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_PendingTurn_MarksCancelledAndRecordsTurnCancelledWithoutFiringCts()
    {
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var request = Request(conversationId, ConversationTurnStatus.Pending, messageId);
        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>()).Returns(true);
        _turnRequestStore.GetActiveAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(new[] { request });
        var sut = CreateSut();

        await sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        await _turnRequestStore.Received(1).MarkCancelledAsync(request.Id, Arg.Any<CancellationToken>());

        // A never-started turn gets its own terminal TurnCancelled event, stamped
        // with its MessageId and null partial content.
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<TurnCancelled>().Single().MessageId == messageId &&
                events.OfType<TurnCancelled>().Single().PartialContent == null),
            Arg.Any<CancellationToken>());

        _activeTurns.DidNotReceive().TryCancel(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_ProcessingTurn_MarksCancelledAndFiresCts()
    {
        var conversationId = Guid.NewGuid();
        var request = Request(conversationId, ConversationTurnStatus.Processing);
        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>()).Returns(true);
        _turnRequestStore.GetActiveAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(new[] { request });
        var sut = CreateSut();

        await sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        await _turnRequestStore.Received(1).MarkCancelledAsync(request.Id, Arg.Any<CancellationToken>());
        _activeTurns.Received(1).TryCancel(conversationId);

        // The Processing turn's TurnCancelled is recorded by the orchestrator, not
        // the handler — the handler records events only for never-started (Pending) turns.
        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ProcessingAndPendingTurns_CancelsBoth()
    {
        // "Stop generating" with turn A Processing and turn B Pending must cancel
        // BOTH: A via the CTS (orchestrator records its event), B directly + a
        // TurnCancelled event stamped with B's MessageId.
        var conversationId = Guid.NewGuid();
        var processing = Request(conversationId, ConversationTurnStatus.Processing, createdAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        var pendingMessageId = Guid.NewGuid();
        var pending = Request(conversationId, ConversationTurnStatus.Pending, pendingMessageId, DateTimeOffset.UtcNow);

        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>()).Returns(true);
        // GetActive returns newest first: pending (B), then processing (A).
        _turnRequestStore.GetActiveAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(new[] { pending, processing });
        var sut = CreateSut();

        await sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        // Both rows marked Cancelled.
        await _turnRequestStore.Received(1).MarkCancelledAsync(pending.Id, Arg.Any<CancellationToken>());
        await _turnRequestStore.Received(1).MarkCancelledAsync(processing.Id, Arg.Any<CancellationToken>());

        // CTS fired once for the Processing turn.
        _activeTurns.Received(1).TryCancel(conversationId);

        // Exactly one TurnCancelled event, for the Pending turn (B), with its MessageId.
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<TurnCancelled>().Single().MessageId == pendingMessageId),
            Arg.Any<CancellationToken>());
    }
}
