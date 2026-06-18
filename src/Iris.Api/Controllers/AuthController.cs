using Iris.Api.Authentication;
using Iris.Application.Identity.DTOs;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.Identity.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Iris.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly AuthCookieService _cookieService;

        public AuthController(IAuthService authService, AuthCookieService cookieService)
        {
            _authService = authService;
            _cookieService = cookieService;
        }

        [HttpGet("social")]
        public IActionResult SocialLogin(LoginProvider provider)
        {
            var properties = new AuthenticationProperties
            {
                RedirectUri = Url.Action(nameof(SocialLoginCallback)),
                Items = { ["LoginProvider"] = provider.ToString() }
            };

            switch (provider)
            {
                case LoginProvider.Google:
                    return Challenge(properties, GoogleDefaults.AuthenticationScheme);
                default:
                    return BadRequest();
            }
        }

        [HttpGet("social/callback")]
        public async Task<IActionResult> SocialLoginCallback(CancellationToken ct = default)
        {
            var result = await HttpContext.AuthenticateAsync("ExternalLogin");
            if (!result.Succeeded)
                return BadRequest("Authentication failed");

            var providerString = result.Properties?.Items["LoginProvider"];
            if (!Enum.TryParse<LoginProvider>(providerString, out var provider))
                return BadRequest("Unknown login provider");

            var authTokens = await _authService.HandleSocialLoginAsync(provider, result.Principal!, ct);
            _cookieService.SetAuthCookies(Response, authTokens);
            return Ok();
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh(CancellationToken ct)
        {
            var refreshToken = Request.Cookies["refresh_token"];

            if (string.IsNullOrWhiteSpace(refreshToken))
                return Unauthorized();

            var authTokens = await _authService.RefreshAsync(refreshToken, ct);
            _cookieService.SetAuthCookies(Response, authTokens);

            return Ok();
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout(CancellationToken ct)
        {
            var refreshToken = Request.Cookies["refresh_token"];

            if (!string.IsNullOrWhiteSpace(refreshToken))
                await _authService.LogoutAsync(refreshToken, ct);

            _cookieService.ClearAuthCookies(Response);

            return NoContent();
        }

        [Authorize]
        [HttpGet("me")]
        public IActionResult Me()
        {
            var userId = User.GetUserId();
            var email = User.FindFirstValue(JwtRegisteredClaimNames.Email);

            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(email))
                return Unauthorized();

            return Ok(new MeResponse
            {
                UserId = userId,
                Email = email
            });
        }
    }
}