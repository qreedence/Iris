using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Notifications;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using MediatR;
using NSubstitute;
using Iris.Domain.Conversations.Content;

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
            new ConversationCreated(aggregateId, Guid.NewGuid(), Guid.NewGuid(), "Chat"),
            new MessageSent(Guid.NewGuid(), aggregateId, MessageContentBlocks.Text("Hello"), ChatRole.User),
            new AssistantResponseCompleted(Guid.NewGuid(), aggregateId, MessageContentBlocks.Text("Hi"), "test/model"),
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

    public static IEnumerable<object[]> RegisteredEventTypes =>
        ConversationEventTypes.ByName.Values.Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(RegisteredEventTypes))]
    public async Task RecordAsync_ForEveryRegisteredEventType_PublishesExactlyOneMatchingNotification(Type eventType)
    {
        // Arrange
        var aggregateId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var evt = CreateSampleEvent(eventType, aggregateId);

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
                        .Select((e, index) => new RecordedEvent(
                            e,
                            index + 1,
                            storedAggregateId,
                            commandId,
                            occurredAt))
                        .ToList());
            });

        var sut = new ConversationEventRecorder(_eventStore, _publisher);

        // Act
        var recorded = await sut.RecordAsync(aggregateId, [evt], CancellationToken.None);

        // Assert
        recorded.Should().ContainSingle().Which.Event.Should().Be(evt);

        var notificationType = typeof(EventNotification<>).MakeGenericType(eventType);
        var publishedNotifications = _publisher.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IPublisher.Publish))
            .Select(call => call.GetArguments()[0])
            .Where(arg => arg is not null)
            .ToList();

        publishedNotifications.Should().ContainSingle(n => n!.GetType() == notificationType);
    }

    private static ConversationEvent CreateSampleEvent(Type eventType, Guid aggregateId)
    {
        object result = eventType.Name switch
        {
            nameof(ConversationCreated) => new ConversationCreated(aggregateId, Guid.NewGuid(), Guid.NewGuid(), "Chat"),
            nameof(MessageSent) => new MessageSent(Guid.NewGuid(), aggregateId, MessageContentBlocks.Text("Hello"), ChatRole.User),
            nameof(AssistantResponseCompleted) => new AssistantResponseCompleted(Guid.NewGuid(), aggregateId, MessageContentBlocks.Text("Hi"), "test/model"),
            nameof(TurnCompleted) => new TurnCompleted(aggregateId, 10, 5),
            nameof(TurnFailed) => new TurnFailed(aggregateId, FailureSource.Provider, "provider_error", "Provider failed.", "partial"),
            nameof(TurnCancelled) => new TurnCancelled(aggregateId, "partial"),
            nameof(ModelChanged) => new ModelChanged(aggregateId, "new/model"),
            nameof(ConversationArchived) => new ConversationArchived(aggregateId),
            _ => throw new NotSupportedException(
                $"No sample event constructor registered in this test for '{eventType.Name}'. " +
                "Add one alongside the new entry in ConversationEventTypes.ByName."),
        };

        return (ConversationEvent)result;
    }
}
