namespace Iris.Application.Personas;

public interface ISystemPromptAssembler
{
    Task<string?> BuildAsync(SystemPromptDto systemPrompt, CancellationToken ct = default);
    Task<string?> BuildAsync(PersonaDto persona, CancellationToken ct = default);
}
