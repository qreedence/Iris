using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging;

namespace Iris.Application.Conversations;

public class ConversationTurnPreparer : IConversationTurnPreparer
{
    private readonly IEventStore _eventStore;
    private readonly IPersonaService _personaService;
    private readonly ISystemPromptAssembler _systemPromptAssembler;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolResultPayloadStore _payloadStore;
    private readonly ILogger<ConversationTurnPreparer> _logger;

    public ConversationTurnPreparer(
        IEventStore eventStore,
        IPersonaService personaService,
        ISystemPromptAssembler systemPromptAssembler,
        IToolRegistry toolRegistry,
        IToolResultPayloadStore payloadStore,
        ILogger<ConversationTurnPreparer> logger)
    {
        _eventStore = eventStore;
        _personaService = personaService;
        _systemPromptAssembler = systemPromptAssembler;
        _toolRegistry = toolRegistry;
        _payloadStore = payloadStore;
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

        var modelChangeAlreadyRecorded = changeModel
            && HasModelChangeForTurn(events, messageId, requestedModel);

        // Scope the stream to this turn's own message: a turn's prompt (model
        // resolution AND message history) must reflect the conversation AS OF its own
        // MessageSent, not messages queued after it. Truncate at that message
        // (inclusive). If the message isn't found, fail open with the full stream —
        // mirroring the worker's idempotency fail-open — after logging a warning.
        events = ScopeToTurn(events, messageId, conversationId);

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
            if (!modelChangeAlreadyRecorded)
                preStreamEvents.Add(new ModelChanged(conversationId, requestedModel));
            effectiveModel = requestedModel;
        }

        var assembledSystemPrompt = await _systemPromptAssembler.BuildAsync(persona, ct);
        var tools = await _toolRegistry.GetToolsForPersonaAsync(persona.Id, ct);

        var chatRequest = new ChatRequest(
            effectiveModel,
            await BuildMessageHistoryAsync(events, messageId, ct),
            assembledSystemPrompt,
            modelParameters,
            tools.Count == 0 ? null : new ToolOptions(tools, ToolChoice.Auto));

        var priorRounds = events.OfType<AssistantResponseCompleted>()
            .Where(response => response.MessageId == messageId)
            .ToList();

        return new PreparedConversationTurn(
            persona.Id,
            chatRequest,
            preStreamEvents,
            priorRounds.Sum(round => round.InputTokens),
            priorRounds.Sum(round => round.OutputTokens));
    }

    private IReadOnlyList<ConversationEvent> ScopeToTurn(
        IReadOnlyList<ConversationEvent> events,
        Guid messageId,
        Guid conversationId)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is MessageSent m && m.Id == messageId)
            {
                return events
                    .Take(i + 1)
                    .Concat(events.Skip(i + 1).Where(evt => BelongsToTurn(evt, messageId)))
                    .ToList();
            }
        }

        _logger.LogWarning(
            "MessageSent {MessageId} for conversation {ConversationId} not found in the event stream; preparing with the full stream",
            messageId,
            conversationId);

        return events;
    }

    private static bool BelongsToTurn(ConversationEvent evt, Guid messageId)
    {
        return evt switch
        {
            AssistantResponseCompleted response => response.MessageId == messageId,
            ToolExecuted toolExecuted => toolExecuted.MessageId == messageId,
            TurnCompleted completed => completed.MessageId == messageId,
            TurnFailed failed => failed.MessageId == messageId,
            TurnCancelled cancelled => cancelled.MessageId == messageId,
            _ => false,
        };
    }

    private static bool HasModelChangeForTurn(
        IReadOnlyList<ConversationEvent> events,
        Guid messageId,
        string requestedModel)
    {
        var messageIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is MessageSent message && message.Id == messageId)
            {
                messageIndex = i;
                break;
            }
        }

        if (messageIndex < 0)
            return false;

        return events
            .Skip(messageIndex + 1)
            .TakeWhile(evt => evt is not MessageSent)
            .OfType<ModelChanged>()
            .Any(evt => evt.Model == requestedModel);
    }

    private async Task<IReadOnlyList<ChatMessage>> BuildMessageHistoryAsync(
        IEnumerable<ConversationEvent> events,
        Guid currentMessageId,
        CancellationToken ct)
    {
        var eventList = events.ToList();
        var completedToolCalls = eventList
            .OfType<ToolExecuted>()
            .Select(evt => (evt.MessageId, evt.ToolCallId))
            .ToHashSet();
        var payloads = await _payloadStore.GetByIdsAsync(
            eventList.OfType<ToolExecuted>().Select(evt => evt.PayloadId),
            ct);
        var messages = new List<ChatMessage>();

        foreach (var evt in eventList)
        {
            switch (evt)
            {
                case MessageSent messageSent:
                    messages.Add(new ChatMessage(
                        messageSent.Role,
                        messageSent.ContentBlocks));
                    break;
                case AssistantResponseCompleted assistantResponseCompleted:
                    var contentBlocks = assistantResponseCompleted.ContentBlocks;
                    if (assistantResponseCompleted.MessageId != currentMessageId)
                    {
                        contentBlocks = contentBlocks
                            .Where(block => block.Type != ContentBlockType.ToolUse
                                || block.ToolCallId is not null
                                && completedToolCalls.Contains((assistantResponseCompleted.MessageId, block.ToolCallId)))
                            .ToList();
                    }

                    if (contentBlocks.Count == 0)
                        break;

                    messages.Add(new ChatMessage(
                        ChatRole.Assistant,
                        contentBlocks));
                    break;
                case ToolExecuted toolExecuted:
                    if (!payloads.TryGetValue(toolExecuted.PayloadId, out var payload))
                    {
                        throw new InvalidOperationException(
                            $"Tool result payload {toolExecuted.PayloadId} was not found.");
                    }

                    messages.Add(new ChatMessage(
                        ChatRole.Tool,
                        [MessageContentBlock.ToolResult(
                            toolExecuted.ToolCallId,
                            toolExecuted.PayloadId,
                            toolExecuted.Name,
                            payload.Preview,
                            toolExecuted.Status,
                            toolExecuted.DurationMs)],
                        payload.PayloadJson));
                    break;
            }
        }

        return messages;
    }
}
