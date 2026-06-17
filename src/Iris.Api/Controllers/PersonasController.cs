using Iris.Api.Authentication;
using Iris.Application.Personas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PersonasController : ControllerBase
{
    private readonly IPersonaService _personaService;

    public PersonasController(IPersonaService personaService)
    {
        _personaService = personaService;
    }

    [HttpPost]
    [ProducesResponseType<PersonaDto>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> Create(
        [FromBody] CreatePersonaRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var persona = await _personaService.CreateAsync(userId, request, ct);
        return CreatedAtAction(nameof(GetById), new { id = persona.Id }, persona);
    }

    [HttpGet]
    [ProducesResponseType<List<PersonaDto>>(200)]
    public async Task<IActionResult> GetAll(
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var personas = await _personaService.GetAllByUserIdAsync(userId, ct);
        return Ok(personas);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PersonaDto>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var persona = await _personaService.GetByIdAsync(userId, id, ct);
        return Ok(persona);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<PersonaDto>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdatePersonaRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var persona = await _personaService.UpdateAsync(userId, id, request, ct);
        return Ok(persona);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        await _personaService.DeleteAsync(userId, id, ct);
        return NoContent();
    }
}
