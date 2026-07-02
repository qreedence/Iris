using Iris.Application.Conversations.Notifications;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Projectors
{
    public class AssistantResponseCompletedProjector : INotificationHandler<EventNotification<AssistantResponseCompleted>>
    {
        private readonly AppDbContext _db;

        public AssistantResponseCompletedProjector(AppDbContext db)
        {
            _db = db;
        }

        public async Task Handle(EventNotification<AssistantResponseCompleted> notification, CancellationToken ct)
        {
            var existing = await _db.ConversationMessages.FindAsync([notification.Event.Id], ct);
            if (existing != null) return; // already projected

            var message = new ConversationMessage
            {
                Id = notification.Event.Id,
                ConversationId = notification.Event.ConversationId,
                Role = ChatRole.Assistant,
                Content = notification.Event.Content,
                CreatedAt = notification.OccurredAt,
            };
            _db.ConversationMessages.Add(message);
            var conversation = await _db.ConversationReadModels.FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);
            if (conversation != null)
            {
                conversation.MessageCount++;
                conversation.LastMessageAt = notification.OccurredAt;
            }
            await _db.SaveChangesAsync(ct);
        }
    }
}
