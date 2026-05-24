using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Personas;

public class PersonaService : IPersonaService
{
    private readonly AppDbContext _db;

    public PersonaService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PersonaDto> GetByIdAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var persona = await _db.Personas
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.Id == id && !p.IsDeleted)
            .Select(p => new PersonaDto(
                p.Id,
                p.Name,
                p.SystemPrompt,
                p.ModelPreference,
                p.Avatar,
                p.CreatedAt,
                p.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        return persona;
    }

    public async Task<PersonaDto> GetForConversationAsync(Guid id, CancellationToken ct = default)
    {
        var persona = await _db.Personas
            .AsNoTracking()
            .Where(p => p.Id == id && !p.IsDeleted)
            .Select(p => new PersonaDto(
                p.Id,
                p.Name,
                p.SystemPrompt,
                p.ModelPreference,
                p.Avatar,
                p.CreatedAt,
                p.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        return persona;
    }

    public async Task<List<PersonaDto>> GetAllByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.Personas
            .AsNoTracking()
            .Where(p => p.UserId == userId && !p.IsDeleted)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PersonaDto(
                p.Id,
                p.Name,
                p.SystemPrompt,
                p.ModelPreference,
                p.Avatar,
                p.CreatedAt,
                p.UpdatedAt))
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
            Avatar = request.Avatar,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Personas.Add(persona);
        await _db.SaveChangesAsync(ct);

        return ToDto(persona);
    }

    public async Task<PersonaDto> UpdateAsync(Guid userId, Guid id, UpdatePersonaRequest request, CancellationToken ct = default)
    {
        ValidateName(request.Name);

        var persona = await _db.Personas
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Id == id && !p.IsDeleted, ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        persona.Name = request.Name;
        persona.SystemPrompt = request.SystemPrompt;
        persona.ModelPreference = request.ModelPreference;
        persona.Avatar = request.Avatar;
        persona.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        return ToDto(persona);
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var persona = await _db.Personas
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Id == id && !p.IsDeleted, ct);

        if (persona is null)
            throw new NotFoundException("Persona not found.");

        var now = DateTimeOffset.UtcNow;
        persona.IsDeleted = true;
        persona.DeletedAt = now;
        persona.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Persona name is required.");
    }

    private static PersonaDto ToDto(Persona persona)
    {
        return new PersonaDto(
            persona.Id,
            persona.Name,
            persona.SystemPrompt,
            persona.ModelPreference,
            persona.Avatar,
            persona.CreatedAt,
            persona.UpdatedAt);
    }
}
