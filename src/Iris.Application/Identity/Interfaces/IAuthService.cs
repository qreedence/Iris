using Iris.Application.Identity.DTOs;
using Iris.Domain.Identity.Enums;
using System.Security.Claims;

namespace Iris.Application.Identity.Interfaces
{
    public interface IAuthService
    {
        Task<AuthTokenResult> HandleSocialLoginAsync(LoginProvider provider, ClaimsPrincipal claims, CancellationToken ct = default);
        //Task LogOutAsync(LogOutRequest request, CancellationToken ct = default);
        //Task<RefreshTokenResponse> RefreshToken(string token);
        //Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct = default);
    }
}
