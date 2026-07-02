using FluentAssertions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iris.Tests.Integration.Conversations;

[Collection("IntegrationTestFactory collection")]
public class EventStoreTests
{
    private readonly IntegrationTestFactory _factory;

    public EventStoreTests(IntegrationTestFactory factory)
    {
        _factory = factory;
    }

    private EfEventStore CreateSut(AppDbContext db) => new(db);

    // --- §1: Append + Persist ---

    [Fact]
    public async Task AppendAsync_SingleEvent_PersistsToDatabaseAndReturnsRecordedMetadata()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var evt = new ConversationCreated(aggregateId, Guid.NewGuid(), Guid.NewGuid(), "Test Chat");

        // Act
        var recorded = await sut.AppendAsync(aggregateId, [evt], commandId, TestContext.Current.CancellationToken);

        // Assert — returned metadata comes from the stored event entity
        var recordedEvent = recorded.Should().ContainSingle().Subject;
        recordedEvent.Event.Should().Be(evt);
        recordedEvent.AggregateId.Should().Be(aggregateId);
        recordedEvent.CommandId.Should().Be(commandId);
        recordedEvent.OccurredAt.Should().NotBe(default);
        recordedEvent.SequenceNumber.Should().BeGreaterThan(0);

        // Assert — query the table directly to verify persistence
        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.StoredEvents
            .SingleOrDefaultAsync(e => e.AggregateId == aggregateId, TestContext.Current.CancellationToken);

        stored.Should().NotBeNull();
        stored!.EventType.Should().Be("ConversationCreated");
        stored.AggregateId.Should().Be(aggregateId);
        stored.CommandId.Should().Be(commandId);
        stored.SequenceNumber.Should().Be(recordedEvent.SequenceNumber);
        stored.OccurredAt.Should().BeCloseTo(recordedEvent.OccurredAt, TimeSpan.FromMilliseconds(1));
        stored.EventData.Should().Contain("Test Chat");
    }

    [Fact]
    public async Task AppendAsync_MultipleEvents_AssignsSequentialSequenceNumbersAndReturnsOrderedMetadata()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var events = new ConversationEvent[]
        {
            new ConversationCreated(aggregateId, Guid.NewGuid(), Guid.NewGuid(), "Chat"),
            new MessageSent(Guid.NewGuid(), aggregateId, "Hello", ChatRole.User),
            new AssistantResponseCompleted(Guid.NewGuid(), aggregateId, "Hi there!", "test/model"),
        };

        // Act
        var recorded = await sut.AppendAsync(aggregateId, events, commandId, TestContext.Current.CancellationToken);

        // Assert
        recorded.Should().HaveCount(3);
        recorded.Select(e => e.Event).Should().Equal(events);
        recorded.Should().AllSatisfy(e =>
        {
            e.AggregateId.Should().Be(aggregateId);
            e.CommandId.Should().Be(commandId);
            e.OccurredAt.Should().NotBe(default);
            e.SequenceNumber.Should().BeGreaterThan(0);
        });
        recorded.Select(e => e.OccurredAt).Distinct().Should().ContainSingle("one append call uses one command timestamp");
        recorded[1].SequenceNumber.Should().Be(recorded[0].SequenceNumber + 1);
        recorded[2].SequenceNumber.Should().Be(recorded[1].SequenceNumber + 1);

        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.StoredEvents
            .Where(e => e.AggregateId == aggregateId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().HaveCount(3);
        stored.Select(e => e.SequenceNumber).Should().Equal(recorded.Select(e => e.SequenceNumber));
    }

    // --- §2: Load + Ordering ---

    [Fact]
    public async Task LoadStreamAsync_ReturnsEventsInSequenceOrder()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var commandId = Guid.NewGuid();

        await sut.AppendAsync(aggregateId, [
            new ConversationCreated(aggregateId, Guid.NewGuid(), Guid.NewGuid(), "Chat")
        ], commandId, TestContext.Current.CancellationToken);

        await sut.AppendAsync(aggregateId, [
            new MessageSent(Guid.NewGuid(), aggregateId, "Hello", ChatRole.User),
            new AssistantResponseCompleted(Guid.NewGuid(), aggregateId, "Hi!", "test/model"),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Act — use a fresh context to ensure we're reading from DB
        await using var readDb = _factory.CreateDbContext();
        var readSut = CreateSut(readDb);
        var stream = await readSut.LoadStreamAsync(aggregateId, TestContext.Current.CancellationToken);

        // Assert
        stream.Should().HaveCount(3);
        stream[0].Should().BeOfType<ConversationCreated>();
        stream[1].Should().BeOfType<MessageSent>();
        stream[2].Should().BeOfType<AssistantResponseCompleted>();
    }

    [Fact]
    public async Task LoadStreamAsync_EmptyStream_ReturnsEmptyCollection()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var nonExistentId = Guid.NewGuid();

        // Act
        var stream = await sut.LoadStreamAsync(nonExistentId, TestContext.Current.CancellationToken);

        // Assert
        stream.Should().NotBeNull();
        stream.Should().BeEmpty();
    }

    // --- §3: Aggregate Isolation ---

    [Fact]
    public async Task LoadStreamAsync_OnlyReturnsEventsForRequestedAggregate()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateA = Guid.NewGuid();
        var aggregateB = Guid.NewGuid();

        await sut.AppendAsync(aggregateA, [
            new ConversationCreated(aggregateA, Guid.NewGuid(), Guid.NewGuid(), "Chat A"),
            new MessageSent(Guid.NewGuid(), aggregateA, "Hello from A", ChatRole.User),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        await sut.AppendAsync(aggregateB, [
            new ConversationCreated(aggregateB, Guid.NewGuid(), Guid.NewGuid(), "Chat B"),
            new MessageSent(Guid.NewGuid(), aggregateB, "Hello from B", ChatRole.User),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Act
        await using var readDb = _factory.CreateDbContext();
        var readSut = CreateSut(readDb);
        var streamA = await readSut.LoadStreamAsync(aggregateA, TestContext.Current.CancellationToken);

        // Assert
        streamA.Should().HaveCount(2);
        streamA.Should().AllSatisfy(e => e.ConversationId.Should().Be(aggregateA));
    }

    // --- §4: Metadata ---

    [Fact]
    public async Task AppendAsync_SetsTimestampAndCorrelationId()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        // Act
        var recorded = await sut.AppendAsync(aggregateId, [
            new ConversationCreated(aggregateId, Guid.NewGuid(), Guid.NewGuid(), "Chat"),
        ], commandId, TestContext.Current.CancellationToken);

        var after = DateTimeOffset.UtcNow;

        // Assert
        var recordedEvent = recorded.Should().ContainSingle().Subject;
        recordedEvent.AggregateId.Should().Be(aggregateId);
        recordedEvent.CommandId.Should().Be(commandId);
        recordedEvent.OccurredAt.Should().BeOnOrAfter(before);
        recordedEvent.OccurredAt.Should().BeOnOrBefore(after);
        recordedEvent.OccurredAt.Offset.Should().Be(TimeSpan.Zero, "timestamp should be UTC");

        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.StoredEvents
            .SingleAsync(e => e.AggregateId == aggregateId, TestContext.Current.CancellationToken);

        stored.CommandId.Should().Be(commandId);
        stored.OccurredAt.Should().BeOnOrAfter(before);
        stored.OccurredAt.Should().BeOnOrBefore(after);
        stored.OccurredAt.Offset.Should().Be(TimeSpan.Zero, "timestamp should be UTC");
    }

    // --- §5: Polymorphic Serialization ---

    [Fact]
    public async Task AppendAsync_DifferentEventTypes_RoundTripsCorrectly()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var personaId = Guid.NewGuid();

        var original = new ConversationEvent[]
        {
            new ConversationCreated(aggregateId, Guid.NewGuid(), personaId, "My Chat"),
            new MessageSent(Guid.NewGuid(), aggregateId, "What is the meaning of life?", ChatRole.User),
            new AssistantResponseCompleted(Guid.NewGuid(), aggregateId, "42, obviously.", "anthropic/claude-sonnet-4"),
            new TurnCompleted(aggregateId, 150, 42),
            new TurnFailed(aggregateId, FailureSource.Provider, "rate_limited", "Rate limit exceeded.", "partial answer"),
            new TurnCancelled(aggregateId, "cancelled partial"),
            new ModelChanged(aggregateId, "openai/gpt-4.1"),
            new ConversationArchived(aggregateId),
        };

        // Act
        await sut.AppendAsync(aggregateId, original, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await using var readDb = _factory.CreateDbContext();
        var readSut = CreateSut(readDb);
        var stream = await readSut.LoadStreamAsync(aggregateId, TestContext.Current.CancellationToken);

        // Assert — verify each event deserializes to the correct type with all properties
        stream.Should().HaveCount(8);

        var created = stream[0].Should().BeOfType<ConversationCreated>().Subject;
        created.ConversationId.Should().Be(aggregateId);
        created.PersonaId.Should().Be(personaId);
        created.Title.Should().Be("My Chat");

        var message = stream[1].Should().BeOfType<MessageSent>().Subject;
        message.Content.Should().Be("What is the meaning of life?");
        message.Role.Should().Be(ChatRole.User);

        var response = stream[2].Should().BeOfType<AssistantResponseCompleted>().Subject;
        response.Content.Should().Be("42, obviously.");
        response.Model.Should().Be("anthropic/claude-sonnet-4");

        var turn = stream[3].Should().BeOfType<TurnCompleted>().Subject;
        turn.InputTokens.Should().Be(150);
        turn.OutputTokens.Should().Be(42);

        var failed = stream[4].Should().BeOfType<TurnFailed>().Subject;
        failed.Source.Should().Be(FailureSource.Provider);
        failed.ErrorCode.Should().Be("rate_limited");
        failed.Message.Should().Be("Rate limit exceeded.");
        failed.PartialContent.Should().Be("partial answer");

        var cancelled = stream[5].Should().BeOfType<TurnCancelled>().Subject;
        cancelled.PartialContent.Should().Be("cancelled partial");
        cancelled.MessageId.Should().BeNull("this round-trip omitted the optional MessageId");

        var modelChanged = stream[6].Should().BeOfType<ModelChanged>().Subject;
        modelChanged.Model.Should().Be("openai/gpt-4.1");

        var archived = stream[7].Should().BeOfType<ConversationArchived>().Subject;
        archived.ConversationId.Should().Be(aggregateId);
    }

    [Fact]
    public async Task LoadStreamAsync_LegacyTurnCancelledWithoutMessageId_DeserializesWithNullMessageId()
    {
        // Back-compat: TurnCancelled events stored before the MessageId field existed
        // have NO messageId property in their JSON and must deserialize to null.
        await using var db = _factory.CreateDbContext();
        var aggregateId = Guid.NewGuid();

        db.StoredEvents.Add(new Iris.Domain.Conversations.StoredEvent
        {
            AggregateId = aggregateId,
            CommandId = Guid.NewGuid(),
            EventType = nameof(TurnCancelled),
            // Legacy payload: only the two original properties, no messageId.
            EventData = $$"""{"ConversationId":"{{aggregateId}}","PartialContent":"legacy partial"}""",
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var readDb = _factory.CreateDbContext();
        var stream = await CreateSut(readDb).LoadStreamAsync(aggregateId, TestContext.Current.CancellationToken);

        var cancelled = stream.Should().ContainSingle().Subject.Should().BeOfType<TurnCancelled>().Subject;
        cancelled.PartialContent.Should().Be("legacy partial");
        cancelled.MessageId.Should().BeNull("legacy events have no messageId property");
    }

    [Fact]
    public async Task AppendAsync_TurnCancelledWithMessageId_RoundTripsMessageId()
    {
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await sut.AppendAsync(
            aggregateId,
            [new TurnCancelled(aggregateId, "partial", messageId)],
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await using var readDb = _factory.CreateDbContext();
        var stream = await CreateSut(readDb).LoadStreamAsync(aggregateId, TestContext.Current.CancellationToken);

        var cancelled = stream.Should().ContainSingle().Subject.Should().BeOfType<TurnCancelled>().Subject;
        cancelled.PartialContent.Should().Be("partial");
        cancelled.MessageId.Should().Be(messageId);
    }

    // --- §6: Multi-Append Continuity ---

    [Fact]
    public async Task AppendAsync_MultipleAppendCalls_ContinuesSequenceNumbering()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();

        // Act — two separate appends
        await sut.AppendAsync(aggregateId, [
            new ConversationCreated(aggregateId, Guid.NewGuid(), Guid.NewGuid(), "Chat"),
            new MessageSent(Guid.NewGuid(), aggregateId, "Hello", ChatRole.User),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        await sut.AppendAsync(aggregateId, [
            new AssistantResponseCompleted(Guid.NewGuid(), aggregateId, "Hi!", "test/model"),
            new TurnCompleted(aggregateId, 10, 5),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert — verify continuous sequence across both appends
        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.StoredEvents
            .Where(e => e.AggregateId == aggregateId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().HaveCount(4);
        for (int i = 1; i < stored.Count; i++)
        {
            stored[i].SequenceNumber.Should().Be(stored[i - 1].SequenceNumber + 1,
                $"event {i} should follow event {i - 1} with no gap");
        }
    }

    // --- §7: Unregistered Event Guard ---

    [Fact]
    public async Task AppendAsync_UnregisteredEventType_ThrowsAndDoesNotPersist()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var unknownEvent = new UnknownConversationEvent(aggregateId);

        // Act
        var act = () => sut.AppendAsync(aggregateId, [unknownEvent], Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*UnknownConversationEvent*");

        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.StoredEvents
            .Where(e => e.AggregateId == aggregateId)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().BeEmpty();
    }

    private record UnknownConversationEvent(Guid ConversationId) : ConversationEvent(ConversationId);
}
