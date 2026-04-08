using Iris.Application.AiIntegration.Models;

namespace Iris.Application.AiIntegration
{
    public interface IChatProvider
    {
        Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);
        IAsyncEnumerable<StreamedChunk> StreamAsync(ChatRequest request, CancellationToken ct = default);
    }
}
