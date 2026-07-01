namespace Iris.Application.Personas;

public interface ISystemPromptAssembler
{
    Task<string?> BuildAsync(SystemPromptDto systemPrompt, CancellationToken ct = default);
}
