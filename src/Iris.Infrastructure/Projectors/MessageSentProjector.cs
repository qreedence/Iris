using Iris.Application.Conversations.Notifications;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;

namespace Iris.Infrastructure.Projectors
{
    public class MessageSentProjector : INotificationHandler<EventNotification<MessageSent>>
    {
        private readonly AppDbContext _db;

        public MessageSentProjector(AppDbContext db)
        {
            _db = db;
        }

        public Task Handle(EventNotification<MessageSent> notification, CancellationToken ct)
        {
            return ConversationMessageProjection.AppendMessageAsync(
                _db,
                notification.Event.Id,
                notification.Event.ConversationId,
                notification.Event.Role,
                notification.Event.Content,
                notification.OccurredAt,
                ct);
        }
    }
}
