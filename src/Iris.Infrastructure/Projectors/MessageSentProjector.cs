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
            var message = new ConversationMessage
            {
                ConversationId = notification.Event.ConversationId,
                Role = notification.Event.Role,
                Content = notification.Event.Content,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.ConversationMessages.Add(message);
            var conversation = await _db.ConversationReadModels.FirstOrDefaultAsync(c => c.Id == message.ConversationId);
            if (conversation != null)
            {
                conversation.MessageCount++;
                conversation.LastMessageAt = DateTimeOffset.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
        }
    }
}
