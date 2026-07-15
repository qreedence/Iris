using Iris.Domain.Conversations.Content;

namespace Iris.Application.AiIntegration.Tools;

public interface IToolResultPayloadStore
{
    void Add(ToolResultPayload payload);

    Task<IReadOnlyDictionary<Guid, ToolResultPayload>> GetByIdsAsync(
        IEnumerable<Guid> payloadIds,
        CancellationToken ct = default);
}
