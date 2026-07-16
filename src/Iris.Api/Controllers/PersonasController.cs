using Iris.Api.Authentication;
using Iris.Application.Exceptions;
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
    private readonly ISystemPromptService _systemPromptService;

    public PersonasController(
        IPersonaService personaService,
        ISystemPromptService systemPromptService)
    {
        _personaService = personaService;
        _systemPromptService = systemPromptService;
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
        var personas = await _personaService.GetAllAsync(ct);
        return Ok(personas);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PersonaDto>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken ct = default)
    {
        var persona = await _personaService.GetByIdAsync(id, ct);
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
        var persona = await _personaService.UpdateAsync(id, request, ct);
        return Ok(persona);
    }

    [HttpGet("{id:guid}/system-prompt")]
    [ProducesResponseType<SystemPromptDto>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetSystemPrompt(
        Guid id,
        CancellationToken ct = default)
    {
        var systemPrompt = await _systemPromptService.GetByPersonaIdAsync(id, ct);
        return Ok(systemPrompt);
    }

    [HttpPut("{id:guid}/system-prompt")]
    [ProducesResponseType<SystemPromptDto>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> UpdateSystemPrompt(
        Guid id,
        [FromBody] SystemPromptSectionsRequest request,
        CancellationToken ct = default)
    {
        var systemPrompt = await _systemPromptService.UpdateAsync(id, request, ct);
        return Ok(systemPrompt);
    }

    [HttpPut("{id:guid}/system-prompt/sections/{section}")]
    [ProducesResponseType<SystemPromptDto>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> UpdateSystemPromptSection(
        Guid id,
        string section,
        [FromBody] UpdateSystemPromptSectionRequest request,
        CancellationToken ct = default)
    {
        if (!SystemPromptSectionParser.TryParse(section, out var parsedSection))
            throw new ValidationException("Invalid system prompt section.");

        var systemPrompt = await _systemPromptService.UpdateSectionAsync(
            id,
            parsedSection,
            request.Content,
            ct);

        return Ok(systemPrompt);
    }

    [HttpDelete("{id:guid}/system-prompt/sections/{section}")]
    [ProducesResponseType<SystemPromptDto>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> ClearSystemPromptSection(
        Guid id,
        string section,
        CancellationToken ct = default)
    {
        if (!SystemPromptSectionParser.TryParse(section, out var parsedSection))
            throw new ValidationException("Invalid system prompt section.");

        var systemPrompt = await _systemPromptService.ClearSectionAsync(
            id,
            parsedSection,
            ct);

        return Ok(systemPrompt);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken ct = default)
    {
        await _personaService.DeleteAsync(id, ct);
        return NoContent();
    }
}
