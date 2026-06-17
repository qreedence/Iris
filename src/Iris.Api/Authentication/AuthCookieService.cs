using Iris.Application.Identity.DTOs;

namespace Iris.Api.Authentication
{
    public class AuthCookieService
    {
        private readonly IWebHostEnvironment _environment;

        public AuthCookieService(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        public void SetAuthCookies(HttpResponse response, AuthTokenResult tokens)
        {
            response.Cookies.Append("access_token", tokens.AccessToken, BuildCookieOptions(tokens.AccessTokenExpiresAt));
            response.Cookies.Append("refresh_token", tokens.RefreshToken, BuildCookieOptions(tokens.RefreshTokenExpiresAt));
        }

        private CookieOptions BuildCookieOptions(DateTimeOffset expiresAt)
        {
            return new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = !_environment.IsDevelopment(),
                Expires = expiresAt
            };
        }
    }
}
