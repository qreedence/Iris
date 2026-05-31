namespace Iris.Application.Conversations;

public interface IConversationTurnQueue
{
    ValueTask EnqueueAsync(ConversationTurnWorkItem workItem, CancellationToken ct = default);

    IAsyncEnumerable<ConversationTurnWorkItem> ReadAllAsync(CancellationToken ct = default);
}
