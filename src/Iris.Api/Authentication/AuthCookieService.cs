using Iris.Application.Identity.DTOs;

namespace Iris.Api.Authentication
{
    public class AuthCookieService
    {
        public const string AccessTokenCookieName = "access_token";
        public const string RefreshTokenCookieName = "refresh_token";

        private readonly IWebHostEnvironment _environment;

        public AuthCookieService(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        public void SetAuthCookies(HttpResponse response, AuthTokenResult tokens)
        {
            response.Cookies.Append(AccessTokenCookieName, tokens.AccessToken, BuildCookieOptions(tokens.AccessTokenExpiresAt));
            response.Cookies.Append(RefreshTokenCookieName, tokens.RefreshToken, BuildCookieOptions(tokens.RefreshTokenExpiresAt));
        }

        public void ClearAuthCookies(HttpResponse response)
        {
            response.Cookies.Delete(AccessTokenCookieName, BuildDeleteCookieOptions());
            response.Cookies.Delete(RefreshTokenCookieName, BuildDeleteCookieOptions());
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

        private CookieOptions BuildDeleteCookieOptions()
        {
            return new CookieOptions
            {
                Path = "/",
                SameSite = SameSiteMode.Lax,
                Secure = !_environment.IsDevelopment()
            };
        }
    }
}
