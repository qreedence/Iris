using Iris.Application.AiIntegration.Tools;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging;

namespace Iris.Application.Conversations;

public class ChatStreamOrchestrator : IChatStreamOrchestrator
{
    private const string PersonaNoLongerExistsMessage = "The persona for this conversation no longer exists.";

    private readonly IConversationTurnPreparer _turnPreparer;
    private readonly IChatProvider _chatProvider;
    private readonly IChatStreamNotifier _notifier;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutionRecorder _toolExecutionRecorder;
    private readonly IActiveTurnRegistry _activeTurns;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatStreamOrchestrator> _logger;

    public ChatStreamOrchestrator(
        IConversationTurnPreparer turnPreparer,
        IChatProvider chatProvider,
        IChatStreamNotifier notifier,
        IConversationEventRecorder eventRecorder,
        IToolRegistry toolRegistry,
        IToolExecutionRecorder toolExecutionRecorder,
        IActiveTurnRegistry activeTurns,
        TimeProvider timeProvider,
        ILogger<ChatStreamOrchestrator> logger)
    {
        _turnPreparer = turnPreparer;
        _chatProvider = chatProvider;
        _notifier = notifier;
        _eventRecorder = eventRecorder;
        _toolRegistry = toolRegistry;
        _toolExecutionRecorder = toolExecutionRecorder;
        _activeTurns = activeTurns;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StreamAsync(
        Guid userId,
        Guid conversationId,
        Guid messageId,
        string model,
        bool changeModel,
        ModelParameters? modelParameters,
        CancellationToken ct = default)
    {
        PreparedConversationTurn preparedTurn;
        try
        {
            preparedTurn = await _turnPreparer.PrepareAsync(userId, conversationId, messageId, model, changeModel, modelParameters, ct);
        }
        catch (ConversationPersonaNotFoundException ex)
        {
            await FailTurnAsync(
                conversationId,
                messageId,
                FailureSource.Internal,
                "persona_not_found",
                PersonaNoLongerExistsMessage,
                partialContent: null);

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

        var request = preparedTurn.ChatRequest;
        string? partialContent = null;
        var totalInputTokens = preparedTurn.PriorInputTokens;
        var totalOutputTokens = preparedTurn.PriorOutputTokens;

        try
        {
            var pendingCalls = FindPendingToolCalls(request.Messages);
            if (pendingCalls.Count > 0)
            {
                request = await ExecuteToolsAsync(
                    request,
                    pendingCalls,
                    userId,
                    preparedTurn.PersonaId,
                    conversationId,
                    messageId,
                    ct);
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                partialContent = null;
                var round = await StreamRoundAsync(
                    request,
                    conversationId,
                    messageId,
                    value => partialContent = value,
                    ct);
                totalInputTokens += round.Usage?.InputTokens ?? 0;
                totalOutputTokens += round.Usage?.OutputTokens ?? 0;

                var assistantResponseCompleted = new AssistantResponseCompleted(
                    Guid.NewGuid(),
                    conversationId,
                    messageId,
                    round.ContentBlocks,
                    request.Model,
                    round.FinishReason,
                    round.Usage?.InputTokens ?? 0,
                    round.Usage?.OutputTokens ?? 0);

                if (round.FinishReason == FinishReason.Stop)
                {
                    var turnCompleted = new TurnCompleted(
                        conversationId,
                        messageId,
                        totalInputTokens,
                        totalOutputTokens,
                        round.Usage?.InputTokens ?? 0);

                    await _eventRecorder.RecordAsync(
                        conversationId,
                        [assistantResponseCompleted, turnCompleted],
                        ct);

                    await _notifier.SendCompletedAsync(conversationId, ct);
                    return;
                }

                // Crash-safety invariant: the model round is durable before any
                // requested tool can produce a side effect.
                await _eventRecorder.RecordAsync(
                    conversationId,
                    [assistantResponseCompleted],
                    ct);

                await SendCompletedToolUseBlocksAsync(
                    conversationId,
                    messageId,
                    round.ContentBlocks,
                    ct);

                request = request with
                {
                    Messages = request.Messages
                        .Append(new ChatMessage(ChatRole.Assistant, round.ContentBlocks))
                        .ToList()
                };

                request = await ExecuteToolsAsync(
                    request,
                    round.ToolCalls,
                    userId,
                    preparedTurn.PersonaId,
                    conversationId,
                    messageId,
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Distinguish a USER cancel ("stop generating", registry flag set) from a
            // host-shutdown interrupt (linked token fired by stoppingToken). Only a
            // user cancel is a terminal outcome worth recording as TurnCancelled; a
            // shutdown interrupt must record NOTHING and rethrow so the worker leaves
            // the row Processing for orphan recovery to resume after restart.
            // Otherwise TurnCancelled would be seen as terminal and the retry skipped,
            // permanently losing (and mislabeling) the turn.
            if (!_activeTurns.WasUserCancelled(conversationId))
            {
                _logger.LogInformation(
                    "Conversation stream for {ConversationId} interrupted (not a user cancel); rethrowing for orphan recovery",
                    conversationId);
                throw;
            }

            var turnCancelled = new TurnCancelled(conversationId, partialContent, messageId);

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

            await FailTurnAsync(conversationId, messageId, source, errorCode, message, partialContent);

            _logger.LogWarning(ex,
                "Conversation stream failed for {ConversationId} with {ErrorCode}",
                conversationId,
                errorCode);

            return;
        }
        catch (Exception ex)
        {
            await FailTurnAsync(
                conversationId,
                messageId,
                FailureSource.Internal,
                "internal_error",
                "An unexpected error occurred.",
                partialContent);

            _logger.LogError(ex, "Unexpected error during streaming for {ConversationId}", conversationId);

            return;
        }

    }

    private async Task<ProviderRound> StreamRoundAsync(
        ChatRequest request,
        Guid conversationId,
        Guid messageId,
        Action<string?> updatePartialContent,
        CancellationToken ct)
    {
        var content = new StreamedContentAccumulator();
        UsageInfo? usage = null;
        IReadOnlyList<ToolCall> toolCalls = [];
        FinishReason? finishReason = null;
        var completed = false;

        await foreach (var chunk in _chatProvider.StreamAsync(request, ct).WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk.Content) || chunk.ProviderMetadata is not null)
            {
                content.Append(chunk);
                updatePartialContent(content.GetPartialVisibleText());

                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    await _notifier.SendChunkAsync(
                        conversationId,
                        new ChatStreamChunkDto(
                            conversationId,
                            messageId,
                            chunk.BlockType,
                            chunk.BlockIndex,
                            chunk.Content),
                        ct);
                }
            }

            usage = chunk.UsageInfo ?? usage;
            toolCalls = chunk.ToolCalls ?? toolCalls;
            finishReason = chunk.FinishReason ?? finishReason;
            completed |= chunk.IsComplete;
        }

        if (!completed)
            throw new ChatProviderException("The provider stream ended before completing the response.");

        finishReason ??= toolCalls.Count > 0 ? FinishReason.ToolCalls : FinishReason.Stop;
        if (finishReason == FinishReason.ToolCalls && toolCalls.Count == 0)
            throw new ChatDeserializationException("Provider ended with ToolCalls but returned no tool calls.");

        var blocks = content.ToContentBlocks().ToList();
        blocks.AddRange(toolCalls.Select(ToToolUseBlock));
        return new ProviderRound(blocks, usage, toolCalls, finishReason.Value);
    }

    private async Task<ChatRequest> ExecuteToolsAsync(
        ChatRequest request,
        IReadOnlyList<ToolCall> toolCalls,
        Guid userId,
        Guid personaId,
        Guid conversationId,
        Guid messageId,
        CancellationToken ct)
    {
        var messages = request.Messages.ToList();
        var context = new ToolContext(userId, personaId, conversationId);

        foreach (var toolCall in toolCalls)
        {
            ct.ThrowIfCancellationRequested();
            var startedAt = _timeProvider.GetTimestamp();
            var result = await _toolRegistry.ExecuteAsync(toolCall, context, ct);
            var durationMs = (long)_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            var toolExecuted = await _toolExecutionRecorder.RecordAsync(
                conversationId,
                messageId,
                toolCall,
                result,
                durationMs,
                ct);

            var resultBlock = MessageContentBlock.ToolResult(
                toolCall.Id,
                toolExecuted.PayloadId,
                toolCall.FunctionName,
                result.Preview,
                result.Status,
                durationMs);
            messages.Add(new ChatMessage(ChatRole.Tool, [resultBlock], result.PayloadJson));

            await _notifier.SendChunkAsync(
                conversationId,
                new ChatStreamChunkDto(
                    conversationId,
                    messageId,
                    ContentBlockType.ToolResult,
                    0,
                    result.Preview,
                    toolCall.Id,
                    toolCall.FunctionName,
                    PayloadId: toolExecuted.PayloadId,
                    Status: result.Status,
                    DurationMs: durationMs),
                ct);
        }

        return request with { Messages = messages };
    }

    private async Task SendCompletedToolUseBlocksAsync(
        Guid conversationId,
        Guid messageId,
        IReadOnlyList<MessageContentBlock> contentBlocks,
        CancellationToken ct)
    {
        for (var i = 0; i < contentBlocks.Count; i++)
        {
            var block = contentBlocks[i];
            if (block.Type != ContentBlockType.ToolUse)
                continue;

            await _notifier.SendChunkAsync(
                conversationId,
                new ChatStreamChunkDto(
                    conversationId,
                    messageId,
                    ContentBlockType.ToolUse,
                    i,
                    null,
                    block.ToolCallId,
                    block.Name,
                    block.ArgumentsJson),
                ct);
        }
    }

    private static IReadOnlyList<ToolCall> FindPendingToolCalls(IReadOnlyList<ChatMessage> messages)
    {
        var currentTurnStart = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                currentTurnStart = i;
                break;
            }
        }

        var currentTurn = messages.Skip(currentTurnStart + 1).ToList();
        var completedCallIds = currentTurn
            .SelectMany(message => message.ContentBlocks)
            .Where(block => block.Type == ContentBlockType.ToolResult && block.ToolCallId is not null)
            .Select(block => block.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        return currentTurn
            .SelectMany(message => message.ContentBlocks)
            .Where(block => block.Type == ContentBlockType.ToolUse
                && block.ToolCallId is not null
                && !completedCallIds.Contains(block.ToolCallId))
            .Select(block => new ToolCall(
                block.ToolCallId!,
                block.Name ?? throw new InvalidOperationException("Tool-use block is missing name."),
                block.ArgumentsJson ?? throw new InvalidOperationException("Tool-use block is missing argumentsJson."),
                GetProviderItemId(block)))
            .ToList();
    }

    private static MessageContentBlock ToToolUseBlock(ToolCall toolCall)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? metadata = toolCall.ProviderItemId is null
            ? null
            : [new Dictionary<string, object?> { ["item_id"] = toolCall.ProviderItemId }];

        return MessageContentBlock.ToolUse(
            toolCall.Id,
            toolCall.FunctionName,
            toolCall.ArgumentsJson,
            metadata);
    }

    private static string? GetProviderItemId(MessageContentBlock block)
    {
        var value = block.ProviderMetadata?
            .SelectMany(metadata => metadata)
            .FirstOrDefault(item => item.Key == "item_id")
            .Value;

        return value switch
        {
            string itemId => itemId,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
    }

    private async Task FailTurnAsync(
        Guid conversationId,
        Guid messageId,
        FailureSource source,
        string errorCode,
        string message,
        string? partialContent)
    {
        var turnFailed = new TurnFailed(conversationId, messageId, source, errorCode, message, partialContent);

        // CancellationToken.None is deliberate: these are cleanup writes that must
        // survive a cancelled outer token so the conversation isn't left without a
        // terminal event and the client isn't left without an error notification.
        await _eventRecorder.RecordAsync(conversationId, [turnFailed], CancellationToken.None);
        await _notifier.SendErrorAsync(conversationId, errorCode, message, CancellationToken.None);
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

    private sealed record ProviderRound(
        IReadOnlyList<MessageContentBlock> ContentBlocks,
        UsageInfo? Usage,
        IReadOnlyList<ToolCall> ToolCalls,
        FinishReason FinishReason);
}
