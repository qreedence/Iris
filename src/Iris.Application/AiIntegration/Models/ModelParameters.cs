namespace Iris.Application.AiIntegration.Models
{
    public record ModelParameters
    (
        float? Temperature,
        int? MaxOutputTokens,
        float? TopP
    );
}
