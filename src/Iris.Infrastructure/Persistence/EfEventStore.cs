using Iris.Application.Conversations;
using Iris.Domain.Conversations;
using Iris.Domain.Conversations.Content;
using Iris.Domain.Conversations.Events;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Infrastructure.Persistence;

public class EfEventStore : IEventStore
{
    private readonly AppDbContext _db;

    public EfEventStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RecordedEvent>> AppendAsync(
        Guid aggregateId,
        IEnumerable<ConversationEvent> events,
        Guid commandId,
        CancellationToken ct = default)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var storedEvents = new List<(StoredEvent StoredEvent, ConversationEvent Event)>();

        foreach (var evt in events)
        {
            if (!ConversationEventTypes.ByName.ContainsKey(evt.GetType().Name))
                throw new InvalidOperationException(
                    $"Unknown conversation event type '{evt.GetType().Name}'.");

            var storedEvent = new StoredEvent
            {
                AggregateId = aggregateId,
                CommandId = commandId,
                EventType = evt.GetType().Name,
                EventData = JsonSerializer.Serialize(evt, evt.GetType(), SerializerOptions),
                OccurredAt = occurredAt,
            };

            _db.StoredEvents.Add(storedEvent);
            storedEvents.Add((storedEvent, evt));
        }

        await _db.SaveChangesAsync(ct);

        return storedEvents
            .Select(e => new RecordedEvent(
                e.Event,
                e.StoredEvent.SequenceNumber,
                e.StoredEvent.AggregateId,
                e.StoredEvent.CommandId,
                e.StoredEvent.OccurredAt))
            .ToList();
    }

    public async Task<IReadOnlyList<ConversationEvent>> LoadStreamAsync(Guid aggregateId, CancellationToken ct = default)
    {
        var storedEvents = await _db.StoredEvents
            .AsNoTracking()
            .Where(e => e.AggregateId == aggregateId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(ct);

        var events = new List<ConversationEvent>();

        foreach (var stored in storedEvents)
        {
            if (!ConversationEventTypes.ByName.TryGetValue(stored.EventType, out var type))
                throw new InvalidOperationException(
                    $"Unknown event type '{stored.EventType}' (sequence {stored.SequenceNumber}, aggregate {stored.AggregateId})");

            var deserialized = JsonSerializer.Deserialize(stored.EventData, type, SerializerOptions)
                as ConversationEvent
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize event '{stored.EventType}' (sequence {stored.SequenceNumber}, aggregate {stored.AggregateId})");

            events.Add(deserialized);
        }

        return events;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter<ContentBlockType>(JsonNamingPolicy.SnakeCaseLower),
            new JsonStringEnumConverter()
        },
    };
}
