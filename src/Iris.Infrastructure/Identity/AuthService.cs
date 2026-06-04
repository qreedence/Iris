using Iris.Application.Identity.Interfaces;
using Iris.Domain.Identity.Enums;
using System.Security.Claims;

namespace Iris.Infrastructure.Identity
{
    public class AuthService : IAuthService
    {
        public Task HandleSocialLoginAsync(LoginProvider provider, ClaimsPrincipal claims, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task HandleSocialLoginAsync(LoginProvider provider, List<Claim> claims, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
