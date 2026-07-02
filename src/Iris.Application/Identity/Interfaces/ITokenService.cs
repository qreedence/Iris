namespace Iris.Application.Identity.Interfaces
{
    public interface ITokenService
    {
        Task<string> GenerateAccessTokenAsync(Guid userId, string email, CancellationToken ct = default);
        Task<string> GenerateRefreshTokenAsync(CancellationToken ct = default);
    }
}
