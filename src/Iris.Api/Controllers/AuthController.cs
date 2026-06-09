using Iris.Application.Identity.Interfaces;
using Iris.Domain.Identity.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;

namespace Iris.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
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

            var authResult = await _authService.HandleSocialLoginAsync(provider, result.Principal!, ct);
            return Ok(authResult);
        }

        //[HttpPost("refresh")]
        //public async Task<IActionResult> Refresh()
        //{

        //}

        //[HttpPost("logout")]
        //public async Task<IActionResult> LogOut()
        //{

        //}

        //[HttpGet("me")]
        //public async Task<IActionResult> Me()
        //{

        //}
    }
}