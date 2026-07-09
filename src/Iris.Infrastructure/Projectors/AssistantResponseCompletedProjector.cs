using Iris.Application.Conversations.Notifications;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;

namespace Iris.Infrastructure.Projectors
{
    public class AssistantResponseCompletedProjector : INotificationHandler<EventNotification<AssistantResponseCompleted>>
    {
        private readonly AppDbContext _db;

        public AssistantResponseCompletedProjector(AppDbContext db)
        {
            _db = db;
        }

        public Task Handle(EventNotification<AssistantResponseCompleted> notification, CancellationToken ct)
        {
            return ConversationMessageProjection.AppendMessageAsync(
                _db,
                notification.Event.Id,
                notification.Event.ConversationId,
                ChatRole.Assistant,
                notification.Event.ContentBlocks,
                notification.OccurredAt,
                ct);
        }
    }
}
