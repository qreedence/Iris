using Iris.Application.AiIntegration.Models;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging;

namespace Iris.Application.Conversations;

public class ConversationTurnPreparer : IConversationTurnPreparer
{
    private readonly IEventStore _eventStore;
    private readonly IPersonaService _personaService;
    private readonly ISystemPromptAssembler _systemPromptAssembler;
    private readonly ILogger<ConversationTurnPreparer> _logger;

    public ConversationTurnPreparer(
        IEventStore eventStore,
        IPersonaService personaService,
        ISystemPromptAssembler systemPromptAssembler,
        ILogger<ConversationTurnPreparer> logger)
    {
        _eventStore = eventStore;
        _personaService = personaService;
        _systemPromptAssembler = systemPromptAssembler;
        _logger = logger;
    }

    public async Task<PreparedConversationTurn> PrepareAsync(
        Guid userId,
        Guid conversationId,
        Guid messageId,
        string requestedModel,
        bool changeModel,
        ModelParameters? modelParameters,
        CancellationToken ct = default)
    {
        var events = await _eventStore.LoadStreamAsync(conversationId, ct);
        if (events.Count == 0)
            throw new NotFoundException("Conversation does not exist.");

        var conversationCreated = events.OfType<ConversationCreated>().FirstOrDefault();
        if (conversationCreated is null || userId != conversationCreated.UserId)
            throw new NotFoundException("Conversation does not exist.");

        // Scope the stream to this turn's own message: a turn's prompt (model
        // resolution AND message history) must reflect the conversation AS OF its own
        // MessageSent, not messages queued after it. Truncate at that message
        // (inclusive). If the message isn't found, fail open with the full stream —
        // mirroring the worker's idempotency fail-open — after logging a warning.
        events = TruncateAtMessage(events, messageId, conversationId);

        PersonaDto persona;
        try
        {
            persona = await _personaService.GetByIdAsync(conversationCreated.PersonaId, ct);
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

        var assembledSystemPrompt = await _systemPromptAssembler.BuildAsync(persona.SystemPrompt, ct);

        var chatRequest = new ChatRequest(
            effectiveModel,
            BuildMessageHistory(events),
            assembledSystemPrompt,
            modelParameters);

        return new PreparedConversationTurn(chatRequest, preStreamEvents);
    }

    private IReadOnlyList<ConversationEvent> TruncateAtMessage(
        IReadOnlyList<ConversationEvent> events,
        Guid messageId,
        Guid conversationId)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is MessageSent m && m.Id == messageId)
                return events.Take(i + 1).ToList();
        }

        _logger.LogWarning(
            "MessageSent {MessageId} for conversation {ConversationId} not found in the event stream; preparing with the full stream",
            messageId,
            conversationId);

        return events;
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
