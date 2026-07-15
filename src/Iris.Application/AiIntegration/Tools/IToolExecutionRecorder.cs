using Iris.Application.AiIntegration.Models;
using Iris.Domain.Conversations.Events;

namespace Iris.Application.AiIntegration.Tools;

public interface IToolExecutionRecorder
{
    Task<ToolExecuted> RecordAsync(
        Guid conversationId,
        Guid messageId,
        ToolCall toolCall,
        ToolResult result,
        long durationMs,
        CancellationToken ct = default);
}
