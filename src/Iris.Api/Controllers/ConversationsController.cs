using Iris.Application.Conversations.Queries;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly IConversationQueries _conversationQueries;

    public ConversationsController(IConversationQueries conversationQueries)
    {
        _conversationQueries = conversationQueries;
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
}
