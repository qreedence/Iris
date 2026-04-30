using Iris.Domain.Conversations.Events;

namespace Iris.Application.Conversations
{
    public interface IEventStore
    {
        public Task AppendAsync(Guid aggregateId, IEnumerable<ConversationEvent> events, Guid commandId, CancellationToken ct);
        public Task<IReadOnlyList<ConversationEvent>> LoadStreamAsync(Guid aggregateId, CancellationToken ct);
    }
}
