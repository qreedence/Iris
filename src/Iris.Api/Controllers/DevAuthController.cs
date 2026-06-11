using System.Security.Claims;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.Identity.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers
{
    /// <summary>
    /// Development-only login that mints real tokens for a test user without
    /// going through Google OAuth, so the frontend can be exercised end to end.
    /// Returns 404 outside the Development environment.
    /// </summary>
    [ApiController]
    [Route("api/dev/auth")]
    public class DevAuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IHostEnvironment _environment;

        public DevAuthController(IAuthService authService, IHostEnvironment environment)
        {
            _authService = authService;
            _environment = environment;
        }

        public sealed record DevLoginRequest(string? Email, string? DisplayName);

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] DevLoginRequest? request, CancellationToken ct = default)
        {
            if (!_environment.IsDevelopment())
                return NotFound();

            var email = string.IsNullOrWhiteSpace(request?.Email) ? "dev@iris.local" : request!.Email!.Trim();
            var displayName = string.IsNullOrWhiteSpace(request?.DisplayName) ? "Iris Dev" : request!.DisplayName!.Trim();

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, $"dev:{email}"),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, displayName)
            }, "DevLogin");

            var result = await _authService.HandleSocialLoginAsync(LoginProvider.Google, new ClaimsPrincipal(identity), ct);
            return Ok(result);
        }
    }
}
