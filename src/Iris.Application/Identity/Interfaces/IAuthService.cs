using Iris.Application.Identity.DTOs;
using Iris.Domain.Identity.Enums;
using System.Security.Claims;

namespace Iris.Application.Identity.Interfaces
{
    public interface IAuthService
    {
        Task<AuthTokenResult> HandleSocialLoginAsync(LoginProvider provider, ClaimsPrincipal claims, CancellationToken ct = default);
        Task<AuthTokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
        Task LogoutAsync(string refreshToken, CancellationToken ct = default);
    }
}
