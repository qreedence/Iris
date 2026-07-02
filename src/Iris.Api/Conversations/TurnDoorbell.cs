using System.Threading.Channels;
using Iris.Application.Conversations;

namespace Iris.Api.Conversations;

/// <summary>
/// Singleton doorbell backed by a capacity-1 channel. Multiple rings between two
/// waits collapse into a single pending signal (FullMode = DropWrite), and a ring
/// that arrives with no waiter is retained so the next WaitAsync returns
/// immediately — the worker never misses a wake-up.
/// </summary>
public class TurnDoorbell : ITurnDoorbell
{
    private readonly Channel<byte> _channel =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Ring()
    {
        _channel.Writer.TryWrite(0);
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _channel.Reader.ReadAsync(ct);
    }
}
