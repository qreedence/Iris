namespace Iris.Application.Personas;

/// <summary>
/// Tenant scoping for reads/updates/deletes comes from the EF global query filter
/// on <c>Persona</c> (keyed on <see cref="Iris.Application.Identity.Interfaces.ICurrentUserService"/>,
/// see AppDbContext.OnModelCreating) — these methods intentionally take no userId.
/// <see cref="CreateAsync"/> keeps userId because it assigns ownership.
/// </summary>
public interface IPersonaService
{
    Task<PersonaDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PersonaDto>> GetAllAsync(CancellationToken ct = default);
    Task<PersonaDto> CreateAsync(Guid userId, CreatePersonaRequest request, CancellationToken ct = default);
    Task<PersonaDto> UpdateAsync(Guid id, UpdatePersonaRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
