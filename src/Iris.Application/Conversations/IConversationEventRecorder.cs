using Iris.Domain.Conversations.Events;

namespace Iris.Application.Conversations;

public interface IConversationEventRecorder
{
    Task<IReadOnlyList<RecordedEvent>> RecordAsync(
        Guid aggregateId,
        IEnumerable<ConversationEvent> events,
        CancellationToken ct = default);
}
