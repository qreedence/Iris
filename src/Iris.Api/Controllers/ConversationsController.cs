using Iris.Api.Authentication;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Commands.StartConversationTurn;
using Iris.Application.Conversations.Queries;
using Iris.Application.Personas;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly IConversationQueries _conversationQueries;
    private readonly IMediator _mediator;
    private readonly IPersonaService _personaService;

    public ConversationsController(
        IConversationQueries conversationQueries,
        IMediator mediator,
        IPersonaService personaService)
    {
        _conversationQueries = conversationQueries;
        _mediator = mediator;
        _personaService = personaService;
    }

    [HttpPost]
    [ProducesResponseType<Guid>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> Create(
        [FromBody] CreateConversationRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        await _personaService.GetByIdAsync(userId, request.PersonaId, ct);

        var conversationId = Guid.NewGuid();
        var command = new CreateConversationCommand(conversationId, userId, request.PersonaId, request.Title);
        await _mediator.Send(command, ct);
        return CreatedAtAction(nameof(GetMessages), new { id = conversationId }, conversationId);
    }

    [HttpGet]
    [ProducesResponseType<List<ConversationSummaryDto>>(200)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var conversations = await _conversationQueries.GetAllAsync(userId, skip, take, ct);
        return Ok(conversations);
    }

    [HttpGet("{id:guid}/messages")]
    [ProducesResponseType<List<ConversationMessageDto>>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetMessages(
        Guid id,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var messages = await _conversationQueries.GetMessagesAsync(userId, id, skip, take, ct);
        if (messages == null) return NotFound();
        return Ok(messages);
    }

    [HttpPost("{id:guid}/chat")]
    [ProducesResponseType(202)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Chat(
        Guid id,
        [FromBody] ChatRequestDto request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var exists = await _conversationQueries.ExistsForUserAsync(userId, id, ct);
        if (!exists) return NotFound();

        var command = new StartConversationTurnCommand(
            id,
            request.UserMessage,
            request.Model,
            request.ChangeModel,
            request.ModelParameters);

        await _mediator.Send(command, ct);

        return Accepted();
    }
}
