using System.Linq.Expressions;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.Personas;

public class PersonaService : IPersonaService
{
    private static readonly Expression<Func<Persona, PersonaDto>> ProjectToDto = p =>
        new PersonaDto(
            p.Id,
            p.Name,
            p.SystemPrompt,
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

    public async Task<PersonaDto> GetByIdAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        return await FindPersonaAsync(id, userId, ct);
    }

    public async Task<PersonaDto> GetForConversationAsync(Guid id, CancellationToken ct = default)
    {
        return await FindPersonaAsync(id, userId: null, ct);
    }

    public async Task<List<PersonaDto>> GetAllByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.Personas
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(ProjectToDto)
            .ToListAsync(ct);
    }

    public async Task<PersonaDto> CreateAsync(CreatePersonaRequest request, CancellationToken ct = default)
    {
        ValidateName(request.Name);

        var now = DateTimeOffset.UtcNow;
        var persona = new Persona
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            Name = request.Name,
            SystemPrompt = request.SystemPrompt,
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

    public async Task<PersonaDto> UpdateAsync(Guid userId, Guid id, UpdatePersonaRequest request, CancellationToken ct = default)
    {
        ValidateName(request.Name);

        var persona = await _db.Personas
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Id == id, ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        persona.Name = request.Name;
        persona.SystemPrompt = request.SystemPrompt;
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

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var persona = await _db.Personas
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Id == id, ct);

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

    private async Task<PersonaDto> FindPersonaAsync(Guid id, Guid? userId, CancellationToken ct)
    {
        var query = _db.Personas
            .AsNoTracking()
            .Where(p => p.Id == id);

        if (userId.HasValue)
            query = query.Where(p => p.UserId == userId.Value);

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
}
