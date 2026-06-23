using Iris.Application.AiIntegration.Models;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;

namespace Iris.Application.Conversations;

public class ConversationTurnPreparer : IConversationTurnPreparer
{
    private readonly IEventStore _eventStore;
    private readonly IPersonaService _personaService;

    public ConversationTurnPreparer(IEventStore eventStore, IPersonaService personaService)
    {
        _eventStore = eventStore;
        _personaService = personaService;
    }

    public async Task<PreparedConversationTurn> PrepareAsync(
        Guid userId,
        Guid conversationId,
        string requestedModel,
        bool changeModel,
        ModelParameters? modelParameters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
            throw new ValidationException("Model can not be empty.");

        var events = await _eventStore.LoadStreamAsync(conversationId, ct);
        if (events.Count == 0)
            throw new NotFoundException("Conversation does not exist.");

        var conversationCreated = events.OfType<ConversationCreated>().FirstOrDefault();
        if (conversationCreated is null || userId != conversationCreated.UserId)
            throw new NotFoundException("Conversation does not exist.");

        PersonaDto persona;
        try
        {
            persona = await _personaService.GetForConversationAsync(conversationCreated.PersonaId, ct);
        }
        catch (NotFoundException)
        {
            throw new ConversationPersonaNotFoundException(conversationId, conversationCreated.PersonaId);
        }

        var preStreamEvents = new List<ConversationEvent>();

        // Model resolution: existing conversation override > persona preference > request fallback.
        // A new ModelChanged event is recorded only when the request explicitly says so.
        var latestModelChanged = events.OfType<ModelChanged>().LastOrDefault()?.Model;
        var effectiveModel = latestModelChanged ?? persona.ModelPreference ?? requestedModel;

        if (changeModel)
        {
            preStreamEvents.Add(new ModelChanged(conversationId, requestedModel));
            effectiveModel = requestedModel;
        }

        var chatRequest = new ChatRequest(
            effectiveModel,
            BuildMessageHistory(events),
            persona.SystemPrompt,
            modelParameters);

        return new PreparedConversationTurn(chatRequest, preStreamEvents);
    }

    private static IReadOnlyList<ChatMessage> BuildMessageHistory(IEnumerable<ConversationEvent> events)
    {
        var messages = new List<ChatMessage>();

        foreach (var evt in events)
        {
            switch (evt)
            {
                case MessageSent messageSent:
                    messages.Add(new ChatMessage(messageSent.Role, messageSent.Content));
                    break;
                case AssistantResponseCompleted assistantResponseCompleted:
                    messages.Add(new ChatMessage(ChatRole.Assistant, assistantResponseCompleted.Content));
                    break;
            }
        }

        return messages;
    }
}
