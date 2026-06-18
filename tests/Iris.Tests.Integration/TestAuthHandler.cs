using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;

namespace Iris.Tests.Integration;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string UserIdHeader = "X-Test-UserId";
    public const string EmailHeader = "X-Test-Email";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Guid userId;
        if (Request.Headers.TryGetValue(UserIdHeader, out var userIdValue)
            && Guid.TryParse(userIdValue, out var headerUserId))
        {
            userId = headerUserId;
        }
        else if (Request.Path.StartsWithSegments("/hubs/chat")
                 && Request.Query.TryGetValue("access_token", out var queryUserIdValue)
                 && Guid.TryParse(queryUserIdValue, out var queryUserId))
        {
            userId = queryUserId;
        }
        else
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var email = Request.Headers.TryGetValue(EmailHeader, out var emailValue)
            ? emailValue.ToString()
            : "test@iris.local";

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
