using Iris.Application.AiIntegration.Models;

namespace Iris.Application.AiIntegration
{
    public interface IChatProvider
    {
        IAsyncEnumerable<StreamedChunk> StreamAsync(ChatRequest request, CancellationToken ct = default);
    }
}
