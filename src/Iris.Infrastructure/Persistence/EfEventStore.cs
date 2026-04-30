using Iris.Application.Conversations;
using Iris.Domain.Conversations;
using Iris.Domain.Conversations.Events;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Iris.Infrastructure.Persistence;

public class EfEventStore : IEventStore
{
    private readonly AppDbContext _db;

    public EfEventStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task AppendAsync(Guid aggregateId, IEnumerable<ConversationEvent> events, Guid commandId, CancellationToken ct)
    {
        foreach (var evt in events)
        {
            _db.StoredEvents.Add(new StoredEvent
            {
                AggregateId = aggregateId,
                CommandId = commandId,
                EventType = evt.GetType().Name,
                EventData = JsonSerializer.Serialize(evt, evt.GetType()),
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConversationEvent>> LoadStreamAsync(Guid aggregateId, CancellationToken ct)
    {
        var storedEvents = await _db.StoredEvents
            .AsNoTracking()
            .Where(e => e.AggregateId == aggregateId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(ct);

        var events = new List<ConversationEvent>();

        foreach (var stored in storedEvents)
        {
            var type = EventTypeMap[stored.EventType];
            var deserialized = JsonSerializer.Deserialize(stored.EventData, type)
                as ConversationEvent
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize event {stored.EventType} (sequence {stored.SequenceNumber})");

            events.Add(deserialized);
        }

        return events;
    }

    private static readonly Dictionary<string, Type> EventTypeMap = new()
    {
        ["ConversationCreated"] = typeof(ConversationCreated),
        ["MessageSent"] = typeof(MessageSent),
        ["AssistantResponseCompleted"] = typeof(AssistantResponseCompleted),
        ["TurnCompleted"] = typeof(TurnCompleted),
        ["ConversationArchived"] = typeof(ConversationArchived),
    };
}

