using System.Threading.Channels;
using Iris.Application.Conversations;

namespace Iris.Api.Conversations;

public class ConversationTurnQueue : IConversationTurnQueue
{
    private readonly Channel<ConversationTurnWorkItem> _queue =
        Channel.CreateUnbounded<ConversationTurnWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(ConversationTurnWorkItem workItem, CancellationToken ct = default)
    {
        return _queue.Writer.WriteAsync(workItem, ct);
    }

    public IAsyncEnumerable<ConversationTurnWorkItem> ReadAllAsync(CancellationToken ct = default)
    {
        return _queue.Reader.ReadAllAsync(ct);
    }
}
