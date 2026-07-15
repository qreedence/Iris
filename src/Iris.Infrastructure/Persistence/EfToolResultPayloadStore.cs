using Iris.Application.AiIntegration.Tools;
using Iris.Domain.Conversations.Content;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Persistence;

public class EfToolResultPayloadStore : IToolResultPayloadStore
{
    private readonly AppDbContext _db;

    public EfToolResultPayloadStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(ToolResultPayload payload)
    {
        _db.ToolResultPayloads.Add(payload);
    }

    public async Task<IReadOnlyDictionary<Guid, ToolResultPayload>> GetByIdsAsync(
        IEnumerable<Guid> payloadIds,
        CancellationToken ct = default)
    {
        var ids = payloadIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, ToolResultPayload>();

        return await _db.ToolResultPayloads
            .AsNoTracking()
            .Where(payload => ids.Contains(payload.Id))
            .ToDictionaryAsync(payload => payload.Id, ct);
    }

}
