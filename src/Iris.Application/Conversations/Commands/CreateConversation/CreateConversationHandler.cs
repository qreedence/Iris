using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.CreateConversation
{
    public class CreateConversationHandler : IRequestHandler<CreateConversationCommand, Guid>
    {
        private readonly IEventStore _eventStore;
        public CreateConversationHandler(IEventStore eventStore)
        {
            _eventStore = eventStore;
        }

        public async Task<Guid> Handle(CreateConversationCommand command, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(command.Title))
            {
                throw new ValidationException("Conversation title is required.");
            }
            if (command.ConversationId == Guid.Empty)
            {
                throw new ValidationException("ConversationId can not be empty.");
            }
            if (command.PersonaId == Guid.Empty)
            {
                throw new ValidationException("PersonaId can not be empty.");
            }
            var conversation = new ConversationCreated(command.ConversationId, command.PersonaId, command.Title);
            await _eventStore.AppendAsync(command.ConversationId, [conversation], Guid.NewGuid(), ct);
            return conversation.ConversationId;
        }
    }
}
