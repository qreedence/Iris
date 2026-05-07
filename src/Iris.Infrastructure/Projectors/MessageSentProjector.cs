using Iris.Application.Conversations.Notifications;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Projectors
{
    public class MessageSentProjector : INotificationHandler<EventNotification<MessageSent>>
    {
        private readonly AppDbContext _db;

        public MessageSentProjector(AppDbContext db)
        {
            _db = db;
        }

        public async Task Handle(EventNotification<MessageSent> notification, CancellationToken ct)
        {
            var existing = await _db.ConversationMessages.FindAsync([notification.Event.Id], ct);
            if (existing != null) return; // already projected

            var message = new ConversationMessage
            {
                Id = notification.Event.Id,
                ConversationId = notification.Event.ConversationId,
                Role = notification.Event.Role,
                Content = notification.Event.Content,
                CreatedAt = notification.OccurredAt
            };
            _db.ConversationMessages.Add(message);
            var conversation = await _db.ConversationReadModels.FirstOrDefaultAsync(c => c.Id == message.ConversationId);
            if (conversation != null)
            {
                conversation.MessageCount++;
                conversation.LastMessageAt = notification.OccurredAt;
            }
            await _db.SaveChangesAsync(ct);
        }
    }
}
