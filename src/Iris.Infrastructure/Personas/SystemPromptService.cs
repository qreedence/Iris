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

        systemPrompt.Identity = SystemPromptSectionContent.Normalize(request.Identity);
        systemPrompt.Voice = SystemPromptSectionContent.Normalize(request.Voice);
        systemPrompt.Role = SystemPromptSectionContent.Normalize(request.Role);
        systemPrompt.Relationship = SystemPromptSectionContent.Normalize(request.Relationship);
        systemPrompt.ToolInstructions = SystemPromptSectionContent.Normalize(request.ToolInstructions);
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

        switch (section)
        {
            case SystemPromptSection.Identity:
                systemPrompt.Identity = normalizedContent;
                break;
            case SystemPromptSection.Voice:
                systemPrompt.Voice = normalizedContent;
                break;
            case SystemPromptSection.Role:
                systemPrompt.Role = normalizedContent;
                break;
            case SystemPromptSection.Relationship:
                systemPrompt.Relationship = normalizedContent;
                break;
            case SystemPromptSection.ToolInstructions:
                systemPrompt.ToolInstructions = normalizedContent;
                break;
            default:
                throw new ValidationException("Invalid system prompt section.");
        }

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
