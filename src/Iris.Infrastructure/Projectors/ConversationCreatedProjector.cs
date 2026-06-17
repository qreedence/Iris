using Iris.Application.Conversations.Notifications;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;

namespace Iris.Infrastructure.Projectors
{
    public class ConversationCreatedProjector : INotificationHandler<EventNotification<ConversationCreated>>
    {
        private readonly AppDbContext _db;

        public ConversationCreatedProjector(AppDbContext db)
        {
            _db = db;
        }

        public async Task Handle(EventNotification<ConversationCreated> notification, CancellationToken ct)
        {
            var existing = await _db.ConversationReadModels.FindAsync([notification.Event.ConversationId], ct);
            if (existing != null) return; // already projected

            var conversation = new ConversationReadModel
            {
                Id = notification.Event.ConversationId,
                UserId = notification.Event.UserId,
                PersonaId = notification.Event.PersonaId,
                CreatedAt = notification.OccurredAt,
                Title = notification.Event.Title,
            };
            _db.ConversationReadModels.Add(conversation);
            await _db.SaveChangesAsync(ct);
        }
    }
}
