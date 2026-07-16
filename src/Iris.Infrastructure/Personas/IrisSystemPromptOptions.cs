namespace Iris.Infrastructure.Personas;

public class IrisSystemPromptOptions
{
    public const string SectionName = "IrisSystemPrompt";
    public string? AppContext { get; init; }
    public string? Guidelines { get; init; }
    public string? Orchestrator { get; init; }
}
