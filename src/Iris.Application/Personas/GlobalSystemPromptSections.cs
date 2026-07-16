namespace Iris.Application.Personas;

public record GlobalSystemPromptSections(
    string? AppContext,
    string? Guidelines,
    string? Orchestrator = null);
