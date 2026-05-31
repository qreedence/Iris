using Iris.Application.Conversations.Notifications;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Projectors;

public class ModelChangedProjector : INotificationHandler<EventNotification<ModelChanged>>
{
    private readonly AppDbContext _db;

    public ModelChangedProjector(AppDbContext db)
    {
        _db = db;
    }

    public async Task Handle(EventNotification<ModelChanged> notification, CancellationToken ct)
    {
        var conversation = await _db.ConversationReadModels
            .FirstOrDefaultAsync(c => c.Id == notification.Event.ConversationId, ct);

        if (conversation is null) return;

        conversation.CurrentModel = notification.Event.Model;
        await _db.SaveChangesAsync(ct);
    }
}
