namespace Iris.Application.Personas;

public interface IPersonaService
{
    Task<PersonaDto> GetByIdAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<PersonaDto> GetForConversationAsync(Guid id, CancellationToken ct = default);
    Task<List<PersonaDto>> GetAllByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<PersonaDto> CreateAsync(CreatePersonaRequest request, CancellationToken ct = default);
    Task<PersonaDto> UpdateAsync(Guid userId, Guid id, UpdatePersonaRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
}
