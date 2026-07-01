using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.Personas;

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
        Guid userId,
        Guid personaId,
        CancellationToken ct = default)
    {
        var systemPrompt = await LoadSystemPromptAsync(userId, personaId, ct);
        return ToDto(systemPrompt);
    }

    public async Task<SystemPromptDto> UpdateAsync(
        Guid userId,
        Guid personaId,
        SystemPromptSectionsRequest request,
        CancellationToken ct = default)
    {
        SystemPromptRequestValidator.EnsureOnlyEditableSections(request);

        var systemPrompt = await LoadSystemPromptAsync(userId, personaId, ct);

        systemPrompt.Identity = NormalizeSection(request.Identity);
        systemPrompt.Voice = NormalizeSection(request.Voice);
        systemPrompt.Role = NormalizeSection(request.Role);
        systemPrompt.Relationship = NormalizeSection(request.Relationship);
        systemPrompt.ToolInstructions = NormalizeSection(request.ToolInstructions);
        systemPrompt.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated system prompt for persona {PersonaId} and user {UserId}",
            personaId,
            userId);

        return ToDto(systemPrompt);
    }

    public async Task<SystemPromptDto> UpdateSectionAsync(
        Guid userId,
        Guid personaId,
        SystemPromptSection section,
        string? content,
        CancellationToken ct = default)
    {
        var systemPrompt = await LoadSystemPromptAsync(userId, personaId, ct);
        var normalizedContent = NormalizeSection(content);

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
            "Updated {Section} system prompt section for persona {PersonaId} and user {UserId}",
            section,
            personaId,
            userId);

        return ToDto(systemPrompt);
    }

    public Task<SystemPromptDto> ClearSectionAsync(
        Guid userId,
        Guid personaId,
        SystemPromptSection section,
        CancellationToken ct = default)
    {
        return UpdateSectionAsync(userId, personaId, section, content: null, ct);
    }

    private async Task<SystemPrompt> LoadSystemPromptAsync(Guid userId, Guid personaId, CancellationToken ct)
    {
        var persona = await _db.Personas
            .Include(p => p.SystemPrompt)
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Id == personaId, ct);

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

    private static string? NormalizeSection(string? content)
    {
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }
}
