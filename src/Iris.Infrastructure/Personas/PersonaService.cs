using System.Linq.Expressions;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.Personas;

/// <summary>
/// Tenant scoping for reads/updates/deletes comes from the EF global query filter
/// on <see cref="Persona"/> (keyed on ICurrentUserService, see AppDbContext.OnModelCreating) —
/// these methods intentionally take no userId. CreateAsync keeps userId because it
/// assigns ownership.
/// </summary>
public class PersonaService : IPersonaService
{
    private static readonly Expression<Func<Persona, PersonaDto>> ProjectToDto = p =>
        new PersonaDto(
            p.Id,
            p.Name,
            p.SystemPrompt == null
                ? SystemPromptDto.Empty
                : new SystemPromptDto(
                    p.SystemPrompt.Identity,
                    p.SystemPrompt.Voice,
                    p.SystemPrompt.Role,
                    p.SystemPrompt.Relationship,
                    p.SystemPrompt.ToolInstructions),
            p.ModelPreference,
            p.Role,
            p.Group,
            p.Avatar,
            p.CreatedAt,
            p.UpdatedAt);

    private static readonly Func<Persona, PersonaDto> MapToDto = ProjectToDto.Compile();

    private readonly AppDbContext _db;
    private readonly ILogger<PersonaService> _logger;

    public PersonaService(AppDbContext db, ILogger<PersonaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PersonaDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await FindPersonaAsync(id, ct);
    }

    public async Task<IReadOnlyList<PersonaDto>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Personas
            .AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .Select(ProjectToDto)
            .ToListAsync(ct);
    }

    public async Task<PersonaDto> CreateAsync(Guid userId, CreatePersonaRequest request, CancellationToken ct = default)
    {
        ValidateName(request.Name);
        SystemPromptRequestValidator.EnsureOnlyEditableSections(request.SystemPrompt);

        var now = DateTimeOffset.UtcNow;
        var persona = new Persona
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name,
            SystemPrompt = CreateSystemPrompt(request.SystemPrompt, now),
            ModelPreference = request.ModelPreference,
            Role = request.Role,
            Group = request.Group,
            Avatar = request.Avatar,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Personas.Add(persona);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created persona {PersonaId} for user {UserId}",
            persona.Id,
            persona.UserId);

        return ToDto(persona);
    }

    public async Task<PersonaDto> UpdateAsync(Guid id, UpdatePersonaRequest request, CancellationToken ct = default)
    {
        ValidateName(request.Name);

        var persona = await _db.Personas
            .Include(p => p.SystemPrompt)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        persona.Name = request.Name;
        persona.ModelPreference = request.ModelPreference;
        persona.Role = request.Role;
        persona.Group = request.Group;
        persona.Avatar = request.Avatar;
        persona.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated persona {PersonaId} for user {UserId}",
            persona.Id,
            persona.UserId);

        return ToDto(persona);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var persona = await _db.Personas
            .Include(p => p.SystemPrompt)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        var now = DateTimeOffset.UtcNow;
        persona.IsDeleted = true;
        persona.DeletedAt = now;
        persona.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Soft deleted persona {PersonaId} for user {UserId}",
            persona.Id,
            persona.UserId);
    }

    private async Task<PersonaDto> FindPersonaAsync(Guid id, CancellationToken ct)
    {
        var query = _db.Personas
            .AsNoTracking()
            .Where(p => p.Id == id);

        return await query.Select(ProjectToDto).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Persona not found.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Persona name is required.");
    }

    private static PersonaDto ToDto(Persona persona)
    {
        return MapToDto(persona);
    }

    private static SystemPrompt CreateSystemPrompt(SystemPromptSectionsRequest? request, DateTimeOffset now)
    {
        var systemPrompt = new SystemPrompt
        {
            CreatedAt = now,
            UpdatedAt = now
        };

        if (request is not null)
        {
            foreach (var definition in SystemPromptSections.All)
            {
                definition.SetOnEntity(systemPrompt, SystemPromptSectionContent.Normalize(definition.GetFromRequest(request)));
            }
        }

        return systemPrompt;
    }
}
