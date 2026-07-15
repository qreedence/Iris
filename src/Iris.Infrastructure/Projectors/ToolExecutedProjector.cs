using Iris.Application.Conversations.Notifications;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Projectors;

public class ToolExecutedProjector : INotificationHandler<EventNotification<ToolExecuted>>
{
    private readonly AppDbContext _db;

    public ToolExecutedProjector(AppDbContext db)
    {
        _db = db;
    }

    public async Task Handle(EventNotification<ToolExecuted> notification, CancellationToken ct)
    {
        var evt = notification.Event;
        var payload = await _db.ToolResultPayloads
            .AsNoTracking()
            .SingleOrDefaultAsync(result => result.Id == evt.PayloadId, ct)
            ?? throw new InvalidOperationException(
                $"Tool result payload {evt.PayloadId} was not found for tool call {evt.ToolCallId}.");

        await ConversationMessageProjection.AppendMessageAsync(
            _db,
            evt.PayloadId,
            evt.ConversationId,
            ChatRole.Tool,
            [MessageContentBlock.ToolResult(
                evt.ToolCallId,
                evt.PayloadId,
                evt.Name,
                payload.Preview,
                evt.Status,
                evt.DurationMs)],
            notification.OccurredAt,
            ct);
    }
}
