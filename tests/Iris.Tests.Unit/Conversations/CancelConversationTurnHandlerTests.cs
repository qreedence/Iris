using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CancelConversationTurn;
using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Entities;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class CancelConversationTurnHandlerTests
{
    private readonly IConversationQueries _conversationQueries = Substitute.For<IConversationQueries>();
    private readonly IConversationTurnRequestStore _turnRequestStore = Substitute.For<IConversationTurnRequestStore>();
    private readonly IActiveTurnRegistry _activeTurns = Substitute.For<IActiveTurnRegistry>();

    private CancelConversationTurnHandler CreateSut() =>
        new(_conversationQueries, _turnRequestStore, _activeTurns);

    private static ConversationTurnRequest Request(Guid conversationId, ConversationTurnStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = Guid.NewGuid(),
            Model = "test/model",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
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
        _turnRequestStore.GetLatestActiveAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns((ConversationTurnRequest?)null);
        var sut = CreateSut();

        await sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        await _turnRequestStore.DidNotReceive().MarkCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _activeTurns.DidNotReceive().TryCancel(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_PendingTurn_MarksCancelledWithoutFiringCts()
    {
        var conversationId = Guid.NewGuid();
        var request = Request(conversationId, ConversationTurnStatus.Pending);
        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>()).Returns(true);
        _turnRequestStore.GetLatestActiveAsync(conversationId, Arg.Any<CancellationToken>()).Returns(request);
        var sut = CreateSut();

        await sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        await _turnRequestStore.Received(1).MarkCancelledAsync(request.Id, Arg.Any<CancellationToken>());
        _activeTurns.DidNotReceive().TryCancel(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handle_ProcessingTurn_MarksCancelledAndFiresCts()
    {
        var conversationId = Guid.NewGuid();
        var request = Request(conversationId, ConversationTurnStatus.Processing);
        _conversationQueries.ExistsForUserAsync(conversationId, Arg.Any<CancellationToken>()).Returns(true);
        _turnRequestStore.GetLatestActiveAsync(conversationId, Arg.Any<CancellationToken>()).Returns(request);
        var sut = CreateSut();

        await sut.Handle(new CancelConversationTurnCommand { ConversationId = conversationId }, CancellationToken.None);

        await _turnRequestStore.Received(1).MarkCancelledAsync(request.Id, Arg.Any<CancellationToken>());
        _activeTurns.Received(1).TryCancel(conversationId);
    }
}
