using Iris.Domain.Conversations.Content;

namespace Iris.Application.AiIntegration.Models
{
    public record StreamedChunk
    (
        string? Content,
        bool IsComplete,
        UsageInfo? UsageInfo,
        ContentBlockType BlockType = ContentBlockType.Text,
        int BlockIndex = 0,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? ProviderMetadata = null
    );
}