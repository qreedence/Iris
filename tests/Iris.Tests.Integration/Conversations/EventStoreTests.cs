using FluentAssertions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iris.Tests.Integration.Conversations;

public class EventStoreTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public EventStoreTests(IntegrationTestFactory factory)
    {
        _factory = factory;
    }

    private EfEventStore CreateSut(AppDbContext db) => new(db);

    // --- §1: Append + Persist ---

    [Fact]
    public async Task AppendAsync_SingleEvent_PersistsToDatabase()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var evt = new ConversationCreated(aggregateId, Guid.NewGuid(), "Test Chat");

        // Act
        await sut.AppendAsync(aggregateId, [evt], commandId, TestContext.Current.CancellationToken);

        // Assert — query the table directly to verify persistence
        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.StoredEvents
            .SingleOrDefaultAsync(e => e.AggregateId == aggregateId, TestContext.Current.CancellationToken);

        stored.Should().NotBeNull();
        stored!.EventType.Should().Be("ConversationCreated");
        stored.AggregateId.Should().Be(aggregateId);
        stored.EventData.Should().Contain("Test Chat");
    }

    [Fact]
    public async Task AppendAsync_MultipleEvents_AssignsSequentialSequenceNumbers()
    {
        // Arrange
        await using var db = _factory.CreateDbContext();
        var sut = CreateSut(db);
        var aggregateId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var events = new ConversationEvent[]
        {
            new ConversationCreated(aggregateId, Guid.NewGuid(), "Chat"),
            new MessageSent(aggregateId, "Hello", ChatRole.User),
            new AssistantResponseCompleted(aggregateId, "Hi there!", "test/model"),
        };

        // Act
        await sut.AppendAsync(aggregateId, events, commandId, TestContext.Current.CancellationToken);

        // Assert
        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.StoredEvents
            .Where(e => e.AggregateId == aggregateId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().HaveCount(3);
        stored[1].SequenceNumber.Should().Be(stored[0].SequenceNumber + 1);
        stored[2].SequenceNumber.Should().Be(stored[1].SequenceNumber + 1);
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
            new ConversationCreated(aggregateId, Guid.NewGuid(), "Chat")
        ], commandId, TestContext.Current.CancellationToken);

        await sut.AppendAsync(aggregateId, [
            new MessageSent(aggregateId, "Hello", ChatRole.User),
            new AssistantResponseCompleted(aggregateId, "Hi!", "test/model"),
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
            new ConversationCreated(aggregateA, Guid.NewGuid(), "Chat A"),
            new MessageSent(aggregateA, "Hello from A", ChatRole.User),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        await sut.AppendAsync(aggregateB, [
            new ConversationCreated(aggregateB, Guid.NewGuid(), "Chat B"),
            new MessageSent(aggregateB, "Hello from B", ChatRole.User),
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
        await sut.AppendAsync(aggregateId, [
            new ConversationCreated(aggregateId, Guid.NewGuid(), "Chat"),
        ], commandId, TestContext.Current.CancellationToken);

        var after = DateTimeOffset.UtcNow;

        // Assert
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
            new ConversationCreated(aggregateId, personaId, "My Chat"),
            new MessageSent(aggregateId, "What is the meaning of life?", ChatRole.User),
            new AssistantResponseCompleted(aggregateId, "42, obviously.", "anthropic/claude-sonnet-4"),
            new TurnCompleted(aggregateId, 150, 42),
            new ConversationArchived(aggregateId),
        };

        // Act
        await sut.AppendAsync(aggregateId, original, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await using var readDb = _factory.CreateDbContext();
        var readSut = CreateSut(readDb);
        var stream = await readSut.LoadStreamAsync(aggregateId, TestContext.Current.CancellationToken);

        // Assert — verify each event deserializes to the correct type with all properties
        stream.Should().HaveCount(5);

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

        var archived = stream[4].Should().BeOfType<ConversationArchived>().Subject;
        archived.ConversationId.Should().Be(aggregateId);
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
            new ConversationCreated(aggregateId, Guid.NewGuid(), "Chat"),
            new MessageSent(aggregateId, "Hello", ChatRole.User),
        ], Guid.NewGuid(), TestContext.Current.CancellationToken);

        await sut.AppendAsync(aggregateId, [
            new AssistantResponseCompleted(aggregateId, "Hi!", "test/model"),
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
}
