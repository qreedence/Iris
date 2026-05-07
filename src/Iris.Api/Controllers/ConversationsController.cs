using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations.Commands.Chat;
using Iris.Application.Conversations.Queries;
using Iris.Application.Exceptions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly IConversationQueries _conversationQueries;
    private readonly IMediator _mediator;

    public ConversationsController(IConversationQueries conversationQueries, IMediator mediator)
    {
        _conversationQueries = conversationQueries;
        _mediator = mediator;
    }

    [HttpGet]
    [ProducesResponseType<List<ConversationSummaryDto>>(200)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var conversations = await _conversationQueries.GetAllAsync(skip, take, ct);
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
        var messages = await _conversationQueries.GetMessagesAsync(id, skip, take, ct);
        if (messages == null) return NotFound();
        return Ok(messages);
    }

    [HttpPost("{id:guid}/chat")]
    [ProducesResponseType<ChatResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Chat(
        Guid id,
        [FromBody] ChatRequestDto request,
        CancellationToken ct = default)
    {
        try
        {
            var command = new ChatCommand(
                id,
                request.UserMessage,
                request.Model,
                request.SystemPrompt,
                request.ModelParameters);

            var response = await _mediator.Send(command, ct);
            return Ok(response);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }
}
