using Iris.Application.Conversations;
using Iris.Application.Conversations.Queries;
using Iris.Application.Conversations.Commands.SendMessage;
using Iris.Domain.AiIntegration;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly IConversationQueries _conversationQueries;
    private readonly IMediator _mediator;
    private readonly IServiceScopeFactory _scopeFactory;

    public ConversationsController(
        IConversationQueries conversationQueries,
        IMediator mediator,
        IServiceScopeFactory scopeFactory)
    {
        _conversationQueries = conversationQueries;
        _mediator = mediator;
        _scopeFactory = scopeFactory;
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
    [ProducesResponseType(202)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Chat(
        Guid id,
        [FromBody] ChatRequestDto request,
        CancellationToken ct = default)
    {
        var command = new SendMessageCommand(
            id,
            request.UserMessage,
            ChatRole.User);

        await _mediator.Send(command, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IChatStreamOrchestrator>();
                await orchestrator.StreamAsync(
                    id,
                    request.Model,
                    request.SystemPrompt,
                    request.ModelParameters,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                using var scope = _scopeFactory.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<ConversationsController>>();
                logger.LogError(ex, "Background streaming failed for conversation {ConversationId}", id);

                try
                {
                    var notifier = scope.ServiceProvider.GetRequiredService<IChatStreamNotifier>();
                    await notifier.SendErrorAsync(id, "internal_error", "Streaming failed to start.", CancellationToken.None);
                }
                catch
                {
                    // Best-effort — if SignalR is also broken, just log
                }
            }
        });

        return Accepted();
    }
}
