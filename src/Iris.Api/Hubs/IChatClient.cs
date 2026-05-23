namespace Iris.Api.Hubs;

public interface IChatClient
{
    Task ReceiveChunk(string content);

    Task ReceiveError(string errorCode, string message);

    Task StreamCompleted();
}
