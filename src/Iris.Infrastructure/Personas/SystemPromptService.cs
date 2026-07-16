using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.Personas;

/// <summary>
/// Tenant scoping comes from the EF global query filter on Persona/SystemPrompt
/// (keyed on ICurrentUserService, see AppDbContext.OnModelCreating) — these methods
/// intentionally take no userId.
/// </summary>
public class SystemPromptService : ISystemPromptService
{
    private readonly AppDbContext _db;
    private readonly ILogger<SystemPromptService> _logger;

    public SystemPromptService(AppDbContext db, ILogger<SystemPromptService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<SystemPromptDto> GetByPersonaIdAsync(
        Guid personaId,
        CancellationToken ct = default)
    {
        var systemPrompt = await LoadSystemPromptAsync(personaId, ct);
        return ToDto(systemPrompt);
    }

    public async Task<SystemPromptDto> UpdateAsync(
        Guid personaId,
        SystemPromptSectionsRequest request,
        CancellationToken ct = default)
    {
        SystemPromptRequestValidator.EnsureOnlyEditableSections(request);

        var systemPrompt = await LoadSystemPromptAsync(personaId, ct);

        foreach (var definition in SystemPromptSections.All)
        {
            definition.SetOnEntity(systemPrompt, SystemPromptSectionContent.Normalize(definition.GetFromRequest(request)));
        }

        systemPrompt.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated system prompt for persona {PersonaId}",
            personaId);

        return ToDto(systemPrompt);
    }

    public async Task<SystemPromptDto> UpdateSectionAsync(
        Guid personaId,
        SystemPromptSection section,
        string? content,
        CancellationToken ct = default)
    {
        var systemPrompt = await LoadSystemPromptAsync(personaId, ct);
        var normalizedContent = SystemPromptSectionContent.Normalize(content);

        var definition = SystemPromptSections.All.SingleOrDefault(d => d.Section == section)
            ?? throw new ValidationException("Invalid system prompt section.");

        definition.SetOnEntity(systemPrompt, normalizedContent);

        systemPrompt.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated {Section} system prompt section for persona {PersonaId}",
            section,
            personaId);

        return ToDto(systemPrompt);
    }

    public Task<SystemPromptDto> ClearSectionAsync(
        Guid personaId,
        SystemPromptSection section,
        CancellationToken ct = default)
    {
        return UpdateSectionAsync(personaId, section, content: null, ct);
    }

    private async Task<SystemPrompt> LoadSystemPromptAsync(Guid personaId, CancellationToken ct)
    {
        var persona = await _db.Personas
            .Include(p => p.SystemPrompt)
            .FirstOrDefaultAsync(p => p.Id == personaId, ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        if (persona.Kind == PersonaKind.System)
            throw new ValidationException("System persona prompts are managed by Iris configuration.");

        return persona.SystemPrompt
            ?? throw new NotFoundException("System prompt not found.");
    }

    private static SystemPromptDto ToDto(SystemPrompt systemPrompt)
    {
        return new SystemPromptDto(
            systemPrompt.Identity,
            systemPrompt.Voice,
            systemPrompt.Role,
            systemPrompt.Relationship,
            systemPrompt.ToolInstructions);
    }
}
