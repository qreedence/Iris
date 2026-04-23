using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly IChatProvider _chatProvider;

        public ChatController (IChatProvider chatProvider)
        {
            _chatProvider = chatProvider;
        }

        [HttpPost]
        [ProducesResponseType<ChatResponse>(200)]
        [ProducesResponseType<ProblemDetails>(500)]
        public async Task<IActionResult> Complete(ChatRequest request, CancellationToken ct)
        {
            var response = await _chatProvider.CompleteAsync(request, ct);
            return Ok(response);
        }
    }
}
