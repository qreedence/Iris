using System;
using System.Collections.Generic;
using System.Text;

namespace Iris.Infrastructure.AiIntegration.Models
{
    internal record OpenRouterRequest(
        string Model,
        List<OpenRouterMessage> Input,
        string? Instructions = null,
        float? Temperature = null,
        int? MaxOutputTokens = null,
        float? TopP = null,
        bool? Stream = null
    );

    internal record OpenRouterMessage(
        string Role,
        string Content
)   ;
}
