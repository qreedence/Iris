using Iris.Domain.Conversations.Events;

namespace Iris.Application.Conversations;

public record RecordedEvent(
    ConversationEvent Event,
    long SequenceNumber,
    Guid AggregateId,
    Guid CommandId,
    DateTimeOffset OccurredAt);
