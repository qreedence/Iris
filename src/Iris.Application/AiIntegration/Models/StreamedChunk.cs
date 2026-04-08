namespace Iris.Application.AiIntegration.Models
{
    public record StreamedChunk
    (
        string? Content,
        bool IsComplete,
        UsageInfo? UsageInfo
    );
}
