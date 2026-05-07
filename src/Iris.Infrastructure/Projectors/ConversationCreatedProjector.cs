using Iris.Application.Conversations.Notifications;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;

namespace Iris.Application.Conversations.Projectors
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
            var conversation = new ConversationReadModel
            {
                Id = notification.Event.ConversationId,
                CreatedAt = DateTimeOffset.UtcNow,
                Title = notification.Event.Title,
            };
            _db.ConversationReadModels.Add(conversation);
            await _db.SaveChangesAsync(ct);
        }
    }
}
