namespace Iris.Application.Personas;

/// <summary>
/// Tenant scoping comes from the EF global query filter on Persona/SystemPrompt
/// (keyed on <see cref="Iris.Application.Identity.Interfaces.ICurrentUserService"/>,
/// see AppDbContext.OnModelCreating) — these methods intentionally take no userId.
/// </summary>
public interface ISystemPromptService
{
    Task<SystemPromptDto> GetByPersonaIdAsync(Guid personaId, CancellationToken ct = default);
    Task<SystemPromptDto> UpdateAsync(Guid personaId, SystemPromptSectionsRequest request, CancellationToken ct = default);
    Task<SystemPromptDto> UpdateSectionAsync(Guid personaId, SystemPromptSection section, string? content, CancellationToken ct = default);
    Task<SystemPromptDto> ClearSectionAsync(Guid personaId, SystemPromptSection section, CancellationToken ct = default);
}
