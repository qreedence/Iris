using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Notifications
{
    public record EventNotification<T>(T Event, DateTimeOffset OccurredAt) : INotification
        where T : ConversationEvent;
}
