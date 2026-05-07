using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.SendMessage
{
    public class SendMessageHandler : IRequestHandler<SendMessageCommand, Unit>
    {
        private readonly IEventStore _eventStore;
        
        public SendMessageHandler(IEventStore eventStore)
        {
            _eventStore = eventStore;
        }

        public async Task<Unit> Handle(SendMessageCommand command, CancellationToken ct)
        {
            if (command.ConversationId == Guid.Empty)
                throw new ValidationException("ConversationId can not be empty.");
            
            if (string.IsNullOrWhiteSpace(command.Content))
                throw new ValidationException("Content can not be empty.");
            
            var events = await _eventStore.LoadStreamAsync(command.ConversationId, ct);
            if (events.Count == 0)
                throw new NotFoundException("Conversation does not exist.");
            
            var message = new MessageSent(command.ConversationId, command.Content, command.Role);
            await _eventStore.AppendAsync(command.ConversationId, [message], Guid.NewGuid(), ct);
            return Unit.Value;
        }
    }
}
