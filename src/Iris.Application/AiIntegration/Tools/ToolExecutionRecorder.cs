using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Domain.Conversations.Content;
using Iris.Domain.Conversations.Events;

namespace Iris.Application.AiIntegration.Tools;

public class ToolExecutionRecorder : IToolExecutionRecorder
{
    private const int MaxPreviewLength = 1000;
    private readonly IToolResultPayloadStore _payloadStore;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly TimeProvider _timeProvider;

    public ToolExecutionRecorder(
        IToolResultPayloadStore payloadStore,
        IConversationEventRecorder eventRecorder,
        TimeProvider timeProvider)
    {
        _payloadStore = payloadStore;
        _eventRecorder = eventRecorder;
        _timeProvider = timeProvider;
    }

    public async Task<ToolExecuted> RecordAsync(
        Guid conversationId,
        Guid messageId,
        ToolCall toolCall,
        ToolResult result,
        long durationMs,
        CancellationToken ct = default)
    {
        var payload = new ToolResultPayload
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            ToolCallId = toolCall.Id,
            PayloadJson = result.PayloadJson,
            Preview = TruncatePreview(result.Preview),
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        var toolExecuted = new ToolExecuted(
            conversationId,
            messageId,
            toolCall.Id,
            toolCall.FunctionName,
            payload.Id,
            result.Status,
            durationMs);

        // The event store's SaveChanges commits this tracked payload and the event
        // together, matching the durable turn-enqueue pattern.
        _payloadStore.Add(payload);
        await _eventRecorder.RecordAsync(conversationId, [toolExecuted], ct);

        return toolExecuted;
    }

    private static string? TruncatePreview(string? preview)
    {
        if (preview is null || preview.Length <= MaxPreviewLength)
            return preview;

        var length = MaxPreviewLength;
        if (char.IsHighSurrogate(preview[length - 1]))
            length--;

        return preview[..length];
    }
}
