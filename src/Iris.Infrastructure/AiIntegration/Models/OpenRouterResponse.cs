using System;
using System.Collections.Generic;
using System.Text;

namespace Iris.Infrastructure.AiIntegration.Models
{
    internal record OpenRouterResponse(
        string Id,
        List<OpenRouterOutputItem> Output,
        OpenRouterUsage? Usage
    );

    internal record OpenRouterOutputItem(
        string Type,
        List<OpenRouterContentBlock>? Content
    );

    internal record OpenRouterContentBlock(
        string Type,
        string? Text
    );

    internal record OpenRouterUsage(
        int InputTokens,
        int OutputTokens,
        int TotalTokens
    );
}
