namespace Iris.Application.Personas;

public interface IGlobalSystemPromptProvider
{
    Task<GlobalSystemPromptSections> GetAsync(CancellationToken ct = default);
}
