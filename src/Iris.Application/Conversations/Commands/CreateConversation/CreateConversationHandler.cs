using Iris.Application.Exceptions;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.CreateConversation
{
    public class CreateConversationHandler : IRequestHandler<CreateConversationCommand, Guid>
    {
        private readonly IEventStore _eventStore;
        private readonly IConversationEventRecorder _eventRecorder;
        
        public CreateConversationHandler(IEventStore eventStore, IConversationEventRecorder eventRecorder)
        {
            _eventStore = eventStore;
            _eventRecorder = eventRecorder;
        }

        public async Task<Guid> Handle(CreateConversationCommand command, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(command.Title))
                throw new ValidationException("Conversation title is required.");
            
            if (command.ConversationId == Guid.Empty)
                throw new ValidationException("ConversationId can not be empty.");

            if (command.UserId == Guid.Empty)
                throw new ValidationException("UserId can not be empty.");
            
            if (command.PersonaId == Guid.Empty)
                throw new ValidationException("PersonaId can not be empty.");
            
            var existingEvents = await _eventStore.LoadStreamAsync(command.ConversationId, ct);
            if (existingEvents.Count > 0)
                throw new ValidationException("Conversation already exists.");

            var conversation = new ConversationCreated(
                command.ConversationId,
                command.UserId,
                command.PersonaId,
                command.Title);

            await _eventRecorder.RecordAsync(command.ConversationId, [conversation], ct);
            return conversation.ConversationId;
        }
    }
}
