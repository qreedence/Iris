using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Infrastructure.AiIntegration.Models;

namespace Iris.Infrastructure.AiIntegration;

public class OpenRouterChatProvider : IChatProvider
{
    private readonly HttpClient _httpClient;

    public OpenRouterChatProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        //var openRouterRequest = new OpenRouterRequest
        //{
        //    Model = request.Model,
        //    Input = request.
        //};
    }

    public IAsyncEnumerable<StreamedChunk> StreamAsync(ChatRequest request, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}