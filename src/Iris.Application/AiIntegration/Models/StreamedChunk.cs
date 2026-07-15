using Iris.Domain.Conversations.Content;
using Iris.Domain.AiIntegration;

namespace Iris.Application.AiIntegration.Models
{
    public record StreamedChunk
    (
        string? Content,
        bool IsComplete,
        UsageInfo? UsageInfo,
        ContentBlockType BlockType = ContentBlockType.Text,
        int BlockIndex = 0,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? ProviderMetadata = null,
        IReadOnlyList<ToolCall>? ToolCalls = null,
        FinishReason? FinishReason = null
    );
}
