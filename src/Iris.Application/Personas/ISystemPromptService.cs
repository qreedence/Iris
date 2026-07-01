namespace Iris.Application.Personas;

public interface ISystemPromptService
{
    Task<SystemPromptDto> GetByPersonaIdAsync(Guid userId, Guid personaId, CancellationToken ct = default);
    Task<SystemPromptDto> UpdateAsync(Guid userId, Guid personaId, SystemPromptSectionsRequest request, CancellationToken ct = default);
    Task<SystemPromptDto> UpdateSectionAsync(Guid userId, Guid personaId, SystemPromptSection section, string? content, CancellationToken ct = default);
    Task<SystemPromptDto> ClearSectionAsync(Guid userId, Guid personaId, SystemPromptSection section, CancellationToken ct = default);
}
