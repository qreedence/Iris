using System.Text;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging;

namespace Iris.Application.Conversations;

public class ChatStreamOrchestrator : IChatStreamOrchestrator
{
    private readonly IConversationTurnPreparer _turnPreparer;
    private readonly IChatProvider _chatProvider;
    private readonly IChatStreamNotifier _notifier;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly ILogger<ChatStreamOrchestrator> _logger;

    public ChatStreamOrchestrator(
        IConversationTurnPreparer turnPreparer,
        IChatProvider chatProvider,
        IChatStreamNotifier notifier,
        IConversationEventRecorder eventRecorder,
        ILogger<ChatStreamOrchestrator> logger)
    {
        _turnPreparer = turnPreparer;
        _chatProvider = chatProvider;
        _notifier = notifier;
        _eventRecorder = eventRecorder;
        _logger = logger;
    }

    public async Task StreamAsync(
        Guid conversationId,
        string model,
        ModelParameters? modelParameters,
        CancellationToken ct = default)
    {
        PreparedConversationTurn preparedTurn;
        try
        {
            preparedTurn = await _turnPreparer.PrepareAsync(conversationId, model, modelParameters, ct);
        }
        catch (ConversationPersonaNotFoundException ex)
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
                ex.PersonaId,
                conversationId);

            return;
        }

        if (preparedTurn.PreStreamEvents.Count > 0)
        {
            await _eventRecorder.RecordAsync(conversationId, preparedTurn.PreStreamEvents, ct);
        }

        var content = new StringBuilder();
        UsageInfo? usageInfo = null;

        try
        {
            await foreach (var chunk in _chatProvider.StreamAsync(preparedTurn.ChatRequest, ct).WithCancellation(ct))
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
            preparedTurn.ChatRequest.Model);

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
