using Iris.Api.Authentication;
using Iris.Application.Exceptions;
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
        private readonly FrontendOptions _frontendOptions;

        public AuthController(
            IAuthService authService,
            AuthCookieService cookieService,
            FrontendOptions frontendOptions)
        {
            _authService = authService;
            _cookieService = cookieService;
            _frontendOptions = frontendOptions;
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
                    throw new ValidationException("Unsupported login provider.");
            }
        }

        [HttpGet("social/callback")]
        public async Task<IActionResult> SocialLoginCallback(CancellationToken ct = default)
        {
            var result = await HttpContext.AuthenticateAsync("ExternalLogin");
            if (!result.Succeeded)
                throw new ValidationException("Authentication failed.");

            var providerString = result.Properties?.Items["LoginProvider"];
            if (!Enum.TryParse<LoginProvider>(providerString, out var provider))
                throw new ValidationException("Unknown login provider.");

            var authTokens = await _authService.HandleSocialLoginAsync(provider, result.Principal!, ct);
            _cookieService.SetAuthCookies(Response, authTokens);

            // Send the browser back to the SPA. The redirect target is static config
            // (never user input), so this cannot be turned into an open redirect.
            return Redirect($"{_frontendOptions.BaseUrl.TrimEnd('/')}/chat");
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh(CancellationToken ct = default)
        {
            var refreshToken = Request.Cookies[AuthCookieService.RefreshTokenCookieName];

            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new UnauthorizedException("Invalid refresh token");

            var authTokens = await _authService.RefreshAsync(refreshToken, ct);
            _cookieService.SetAuthCookies(Response, authTokens);

            return Ok();
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout(CancellationToken ct = default)
        {
            var refreshToken = Request.Cookies[AuthCookieService.RefreshTokenCookieName];

            if (!string.IsNullOrWhiteSpace(refreshToken))
                await _authService.LogoutAsync(refreshToken, ct);

            _cookieService.ClearAuthCookies(Response);

            return Ok();
        }

        [Authorize]
        [HttpGet("me")]
        public IActionResult Me()
        {
            var userId = User.GetUserId();
            var email = User.FindFirstValue(JwtRegisteredClaimNames.Email);

            if (string.IsNullOrWhiteSpace(email))
                throw new UnauthorizedException("Invalid user");

            return Ok(new MeResponse
            {
                UserId = userId,
                Email = email
            });
        }
    }
}
