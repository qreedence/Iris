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
        public async Task<IActionResult> SocialLogin(LoginProvider provider)
        {
            switch (provider)
            {
                case LoginProvider.Google:
                    {
                        var properties = new AuthenticationProperties
                        {
                            RedirectUri = Url.Action(nameof(SocialLoginCallback))
                        };
                        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
                    }
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

            var claims = result.Principal.Claims
                .Select(c => new { c.Type, c.Value })
                .ToList();

            return Ok(claims);
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