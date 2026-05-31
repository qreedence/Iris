using Iris.Domain.Conversations.Events;

namespace Iris.Application.Conversations
{
    public interface IEventStore
    {
        Task AppendAsync(Guid aggregateId, IEnumerable<ConversationEvent> events, Guid commandId, CancellationToken ct = default);
        Task<IReadOnlyList<ConversationEvent>> LoadStreamAsync(Guid aggregateId, CancellationToken ct = default);
    }
}
