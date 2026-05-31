using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Notifications;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using MediatR;
using NSubstitute;

namespace Iris.Tests.Unit.Conversations;

public class ConversationEventRecorderTests
{
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IPublisher _publisher = Substitute.For<IPublisher>();

    [Fact]
    public async Task RecordAsync_AppendsWithGeneratedCommandIdAndPublishesRecordedEvents()
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var events = new ConversationEvent[]
        {
            new ConversationCreated(aggregateId, Guid.NewGuid(), "Chat"),
            new MessageSent(Guid.NewGuid(), aggregateId, "Hello", ChatRole.User),
            new AssistantResponseCompleted(Guid.NewGuid(), aggregateId, "Hi", "test/model"),
            new TurnCompleted(aggregateId, 10, 5),
            new TurnFailed(aggregateId, FailureSource.Provider, "provider_error", "Provider failed.", "partial"),
            new TurnCancelled(aggregateId, "partial"),
            new ModelChanged(aggregateId, "new/model"),
            new ConversationArchived(aggregateId),
        };

        _eventStore.AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var storedAggregateId = call.ArgAt<Guid>(0);
                var storedEvents = call.ArgAt<IEnumerable<ConversationEvent>>(1).ToList();
                var commandId = call.ArgAt<Guid>(2);

                return Task.FromResult<IReadOnlyList<RecordedEvent>>(
                    storedEvents
                        .Select((evt, index) => new RecordedEvent(
                            evt,
                            index + 1,
                            storedAggregateId,
                            commandId,
                            occurredAt))
                        .ToList());
            });
        _eventStore.ClearReceivedCalls();

        var sut = new ConversationEventRecorder(_eventStore, _publisher);

        // Act
        var recorded = await sut.RecordAsync(aggregateId, events, CancellationToken.None);

        // Assert
        recorded.Should().HaveCount(events.Length);
        recorded.Select(e => e.Event).Should().Equal(events);
        recorded.Should().AllSatisfy(e =>
        {
            e.AggregateId.Should().Be(aggregateId);
            e.CommandId.Should().NotBeEmpty();
            e.OccurredAt.Should().Be(occurredAt);
        });

        var commandId = recorded[0].CommandId;
        recorded.Select(e => e.CommandId).Distinct().Should().ContainSingle();

        await _eventStore.Received(1).AppendAsync(
            aggregateId,
            Arg.Is<IEnumerable<ConversationEvent>>(storedEvents => storedEvents.SequenceEqual(events)),
            Arg.Is<Guid>(id => id == commandId && id != Guid.Empty),
            CancellationToken.None);

        await _publisher.Received(1).Publish(
            Arg.Is<EventNotification<ConversationCreated>>(n =>
                n.Event == events[0] &&
                n.AggregateId == aggregateId &&
                n.CommandId == commandId &&
                n.SequenceNumber == 1 &&
                n.OccurredAt == occurredAt),
            CancellationToken.None);
        await _publisher.Received(1).Publish(Arg.Any<EventNotification<MessageSent>>(), CancellationToken.None);
        await _publisher.Received(1).Publish(Arg.Any<EventNotification<AssistantResponseCompleted>>(), CancellationToken.None);
        await _publisher.Received(1).Publish(Arg.Any<EventNotification<TurnCompleted>>(), CancellationToken.None);
        await _publisher.Received(1).Publish(Arg.Any<EventNotification<TurnFailed>>(), CancellationToken.None);
        await _publisher.Received(1).Publish(Arg.Any<EventNotification<TurnCancelled>>(), CancellationToken.None);
        await _publisher.Received(1).Publish(Arg.Any<EventNotification<ModelChanged>>(), CancellationToken.None);
        await _publisher.Received(1).Publish(Arg.Any<EventNotification<ConversationArchived>>(), CancellationToken.None);
    }

    [Fact]
    public async Task RecordAsync_UnknownEventType_ThrowsAndDoesNotAppend()
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var sut = new ConversationEventRecorder(_eventStore, _publisher);
        var unknownEvent = new UnknownConversationEvent(aggregateId);

        // Act
        var act = () => sut.RecordAsync(aggregateId, [unknownEvent], CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*UnknownConversationEvent*");

        await _eventStore.DidNotReceive().AppendAsync(
            Arg.Any<Guid>(),
            Arg.Any<IEnumerable<ConversationEvent>>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    private record UnknownConversationEvent(Guid ConversationId) : ConversationEvent(ConversationId);
}
