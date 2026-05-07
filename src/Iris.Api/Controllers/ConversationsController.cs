using Iris.Application.Conversations.Queries;
using Iris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iris.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ConversationsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType<List<ConversationSummaryDto>>(200)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var conversations = await _db.ConversationReadModels
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ConversationSummaryDto(
                c.Id,
                c.Title,
                c.CreatedAt,
                c.MessageCount,
                c.LastMessageAt))
            .ToListAsync(ct);

        return Ok(conversations);
    }

    [HttpGet("{id:guid}/messages")]
    [ProducesResponseType<List<ConversationMessageDto>>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetMessages(Guid id, CancellationToken ct)
    {
        var exists = await _db.ConversationReadModels
            .AsNoTracking()
            .AnyAsync(c => c.Id == id, ct);

        if (!exists)
            return NotFound();

        var messages = await _db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ConversationMessageDto(
                m.Id,
                m.ConversationId,
                m.Role,
                m.Content,
                m.CreatedAt))
            .ToListAsync(ct);

        return Ok(messages);
    }
}
