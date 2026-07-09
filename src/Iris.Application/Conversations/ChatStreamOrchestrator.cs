using System.Text;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
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
    private readonly IActiveTurnRegistry _activeTurns;
    private readonly ILogger<ChatStreamOrchestrator> _logger;

    public ChatStreamOrchestrator(
        IConversationTurnPreparer turnPreparer,
        IChatProvider chatProvider,
        IChatStreamNotifier notifier,
        IConversationEventRecorder eventRecorder,
        IActiveTurnRegistry activeTurns,
        ILogger<ChatStreamOrchestrator> logger)
    {
        _turnPreparer = turnPreparer;
        _chatProvider = chatProvider;
        _notifier = notifier;
        _eventRecorder = eventRecorder;
        _activeTurns = activeTurns;
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

        var content = new StreamedContentAccumulator();
        UsageInfo? usageInfo = null;

        try
        {
            await foreach (var chunk in _chatProvider.StreamAsync(preparedTurn.ChatRequest, ct).WithCancellation(ct))
            {
                if (!string.IsNullOrEmpty(chunk.Content) || chunk.ProviderMetadata is not null)
                {
                    content.Append(chunk);

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

                if (chunk.UsageInfo is not null)
                    usageInfo = chunk.UsageInfo;
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

            var turnCancelled = new TurnCancelled(conversationId, content.GetPartialVisibleText(), messageId);

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

            await FailTurnAsync(conversationId, source, errorCode, message, content.GetPartialVisibleText());

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
                FailureSource.Internal,
                "internal_error",
                "An unexpected error occurred.",
                content.GetPartialVisibleText());

            _logger.LogError(ex, "Unexpected error during streaming for {ConversationId}", conversationId);

            return;
        }

        var assistantResponseCompleted = new AssistantResponseCompleted(
            Guid.NewGuid(),
            conversationId,
            content.ToContentBlocks(),
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

    private async Task FailTurnAsync(
        Guid conversationId,
        FailureSource source,
        string errorCode,
        string message,
        string? partialContent)
    {
        var turnFailed = new TurnFailed(conversationId, source, errorCode, message, partialContent);

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

    private sealed class StreamedContentAccumulator
    {
        private readonly SortedDictionary<int, MutableBlock> _blocks = [];

        public void Append(StreamedChunk chunk)
        {
            if (!_blocks.TryGetValue(chunk.BlockIndex, out var block))
            {
                block = new MutableBlock(chunk.BlockType);
                _blocks[chunk.BlockIndex] = block;
            }

            block.Append(chunk.Content, chunk.ProviderMetadata);
        }

        public IReadOnlyList<MessageContentBlock> ToContentBlocks()
        {
            return _blocks.Values
                .Select(block => block.ToContentBlock())
                .ToList();
        }

        public string? GetPartialVisibleText()
        {
            var visibleText = string.Concat(_blocks.Values
                .Where(block => block.Type == ContentBlockType.Text)
                .Select(block => block.Content.ToString()));

            return string.IsNullOrEmpty(visibleText) ? null : visibleText;
        }
    }

    private sealed class MutableBlock
    {
        private readonly List<IReadOnlyDictionary<string, object?>> _providerMetadata = [];

        public MutableBlock(ContentBlockType type)
        {
            Type = type;
        }

        public ContentBlockType Type { get; }
        public StringBuilder Content { get; } = new();

        public void Append(string? content, IReadOnlyList<IReadOnlyDictionary<string, object?>>? providerMetadata)
        {
            if (!string.IsNullOrEmpty(content))
                Content.Append(content);

            if (providerMetadata is { } metadata)
                _providerMetadata.AddRange(metadata);
        }

        public MessageContentBlock ToContentBlock()
        {
            var content = Content.ToString();
            var metadata = BuildProviderMetadata();

            return Type switch
            {
                ContentBlockType.Text => MessageContentBlock.Text(content),
                ContentBlockType.Thinking => MessageContentBlock.Thinking(content, metadata),
                _ => new MessageContentBlock
                {
                    Type = Type,
                    Content = content,
                    ProviderMetadata = metadata,
                }
            };
        }

        private IReadOnlyList<IReadOnlyDictionary<string, object?>>? BuildProviderMetadata()
        {
            return _providerMetadata.Count == 0
                ? null
                : _providerMetadata;
        }
    }
}
