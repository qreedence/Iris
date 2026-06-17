using Iris.Application.Exceptions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Iris.Api.Authentication
{
    public static class ClaimsPrincipalExtensions
    {
        public static Guid GetUserId(this ClaimsPrincipal user)
        {
            var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!Guid.TryParse(userId, out var parsedUserId))
                throw new UnauthorizedException("Invalid user");

            return parsedUserId;
        } 
    }
}
