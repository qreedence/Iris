namespace Iris.Infrastructure.AiIntegration;

public class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";
    public required string ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://openrouter.ai";
    public string AppTitle { get; init; } = "Iris";
    public string AppUrl { get; init; } = "https://iris.qreedence.com";
}