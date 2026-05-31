using System.Text;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging;

namespace Iris.Application.Conversations;

public class ChatStreamOrchestrator : IChatStreamOrchestrator
{
    private readonly IEventStore _eventStore;
    private readonly IChatProvider _chatProvider;
    private readonly IChatStreamNotifier _notifier;
    private readonly IPersonaService _personaService;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly ILogger<ChatStreamOrchestrator> _logger;

    public ChatStreamOrchestrator(
        IEventStore eventStore,
        IChatProvider chatProvider,
        IChatStreamNotifier notifier,
        IPersonaService personaService,
        IConversationEventRecorder eventRecorder,
        ILogger<ChatStreamOrchestrator> logger)
    {
        _eventStore = eventStore;
        _chatProvider = chatProvider;
        _notifier = notifier;
        _personaService = personaService;
        _eventRecorder = eventRecorder;
        _logger = logger;
    }

    public async Task StreamAsync(
        Guid conversationId,
        string model,
        ModelParameters? modelParameters,
        CancellationToken ct = default)
    {
        var events = await _eventStore.LoadStreamAsync(conversationId, ct);
        if (events.Count == 0)
            throw new NotFoundException("Conversation does not exist.");

        var conversationCreated = events.OfType<ConversationCreated>().FirstOrDefault();
        if (conversationCreated is null)
            throw new NotFoundException("Conversation does not exist.");

        PersonaDto persona;
        try
        {
            persona = await _personaService.GetForConversationAsync(conversationCreated.PersonaId, ct);
        }
        catch (NotFoundException)
        {
            var turnFailed = new TurnFailed(
                conversationId,
                FailureSource.Internal,
                "persona_not_found",
                "The persona for this conversation no longer exists.",
                null);

            await _eventRecorder.RecordAsync(conversationId, [turnFailed], CancellationToken.None);
            await _notifier.SendErrorAsync(
                conversationId,
                "persona_not_found",
                "The persona for this conversation no longer exists.",
                CancellationToken.None);

            _logger.LogWarning(
                "Persona {PersonaId} for conversation {ConversationId} was not found",
                conversationCreated.PersonaId,
                conversationId);

            return;
        }

        // Model resolution: ModelChanged (explicit override) > persona preference > request fallback
        var latestModelChanged = events.OfType<ModelChanged>().LastOrDefault()?.Model;
        var effectiveModel = latestModelChanged ?? persona.ModelPreference ?? model;

        // If the request model differs from the effective model, the user is switching
        if (model != effectiveModel)
        {
            var modelChanged = new ModelChanged(conversationId, model);
            await _eventRecorder.RecordAsync(conversationId, [modelChanged], ct);
            effectiveModel = model;
        }

        var chatRequest = new ChatRequest(
            effectiveModel,
            BuildMessageHistory(events),
            persona.SystemPrompt,
            modelParameters);

        var content = new StringBuilder();
        UsageInfo? usageInfo = null;

        try
        {
            await foreach (var chunk in _chatProvider.StreamAsync(chatRequest, ct).WithCancellation(ct))
            {
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    content.Append(chunk.Content);
                    await _notifier.SendChunkAsync(conversationId, chunk.Content, ct);
                }

                if (chunk.UsageInfo is not null)
                    usageInfo = chunk.UsageInfo;
            }
        }
        catch (OperationCanceledException)
        {
            var turnCancelled = new TurnCancelled(conversationId, GetPartialContent(content));

            await _eventRecorder.RecordAsync(
                conversationId,
                [turnCancelled],
                CancellationToken.None);

            _logger.LogInformation("Conversation stream cancelled for {ConversationId}", conversationId);

            return;
        }
        catch (ChatProviderException ex)
        {
            var (source, errorCode, message) = MapFailure(ex);
            var turnFailed = new TurnFailed(
                conversationId,
                source,
                errorCode,
                message,
                GetPartialContent(content));

            await _eventRecorder.RecordAsync(conversationId, [turnFailed], CancellationToken.None);
            await _notifier.SendErrorAsync(conversationId, errorCode, message, CancellationToken.None);

            _logger.LogWarning(ex,
                "Conversation stream failed for {ConversationId} with {ErrorCode}",
                conversationId,
                errorCode);

            return;
        }
        catch (Exception ex)
        {
            var turnFailed = new TurnFailed(
                conversationId,
                FailureSource.Internal,
                "internal_error",
                "An unexpected error occurred.",
                GetPartialContent(content));

            await _eventRecorder.RecordAsync(conversationId, [turnFailed], CancellationToken.None);
            await _notifier.SendErrorAsync(conversationId, "internal_error", "An unexpected error occurred.", CancellationToken.None);
            _logger.LogError(ex, "Unexpected error during streaming for {ConversationId}", conversationId);

            return;
        }


        var assistantResponseCompleted = new AssistantResponseCompleted(
            Guid.NewGuid(),
            conversationId,
            content.ToString(),
            chatRequest.Model);

        var turnCompleted = new TurnCompleted(
            conversationId,
            usageInfo?.InputTokens ?? 0,
            usageInfo?.OutputTokens ?? 0);

        await _eventRecorder.RecordAsync(
            conversationId,
            [assistantResponseCompleted, turnCompleted],
            ct);

        await _notifier.SendCompletedAsync(conversationId, ct);
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

    private static (FailureSource Source, string ErrorCode, string Message) MapFailure(ChatProviderException ex)
    {
        return ex switch
        {
            ChatTimeoutException => (FailureSource.Timeout, "provider_timeout", "The AI provider timed out."),
            ChatRateLimitException => (FailureSource.Provider, "rate_limited", "The AI provider rate limit was exceeded."),
            ChatAuthenticationException => (FailureSource.Provider, "provider_authentication_failed", "The AI provider rejected authentication."),
            ChatDeserializationException => (FailureSource.Provider, "provider_response_invalid", "The AI provider returned an invalid response."),
            _ => (FailureSource.Provider, "provider_error", ex.Message)
        };
    }

    private static string? GetPartialContent(StringBuilder content)
    {
        return content.Length == 0 ? null : content.ToString();
    }
}
