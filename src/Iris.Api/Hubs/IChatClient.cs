using Iris.Application.Conversations;

namespace Iris.Api.Hubs;

public interface IChatClient
{
    Task ReceiveChunk(ChatStreamChunkDto chunk);

    Task ReceiveError(ChatStreamErrorDto error);

    Task StreamCompleted(ChatStreamCompletedDto completed);
}