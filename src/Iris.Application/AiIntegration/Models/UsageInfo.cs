namespace Iris.Application.AiIntegration.Models
{
    public record UsageInfo
    (
        int InputTokens,
        int OutputTokens,
        int TotalTokens
    );
}
