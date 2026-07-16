using Iris.Application.Exceptions;
using Iris.Application.Identity.DTOs;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.Identity.Entities;
using Iris.Domain.Identity.Enums;
using Iris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Iris.Application.Personas;

namespace Iris.Infrastructure.Identity
{
    public class AuthService : IAuthService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly JwtOptions _jwtOptions;
        private readonly AppDbContext _db;
        private readonly ITokenService _tokenService;
        private readonly IOrchestratorProvisioner _orchestratorProvisioner;

        public AuthService(
            UserManager<ApplicationUser> userManager,
            IOptions<JwtOptions> jwtOptions,
            AppDbContext db,
            ITokenService tokenService,
            IOrchestratorProvisioner orchestratorProvisioner)
        {
            _userManager = userManager;
            _jwtOptions = jwtOptions.Value;
            _db = db;
            _tokenService = tokenService;
            _orchestratorProvisioner = orchestratorProvisioner;
        }

        public async Task<AuthTokenResult> HandleSocialLoginAsync(LoginProvider provider, ClaimsPrincipal claims, CancellationToken ct = default)
        {
            var email = claims.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrEmpty(email))
                throw new ValidationException("Invalid email");

            var providerUserId = claims.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(providerUserId))
                throw new ValidationException("Invalid provider user id");

            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                user = new ApplicationUser
                {
                    Email = email,
                    UserName = email,
                    DisplayName = claims.FindFirst(ClaimTypes.Name)?.Value
                };

                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                    throw new ValidationException(createResult.Errors.First().Description);
            }

            var providerName = provider.ToString();
            var logins = await _userManager.GetLoginsAsync(user);
            if (!logins.Any(login => login.LoginProvider == providerName))
            {
                var loginInfo = new UserLoginInfo(providerName, providerUserId, providerName);
                var addLoginResult = await _userManager.AddLoginAsync(user, loginInfo);
                if (!addLoginResult.Succeeded)
                    throw new ValidationException(addLoginResult.Errors.First().Description);
            }

            // Login is also the repair path for users created before orchestrator
            // provisioning existed or left partially provisioned by an earlier failure.
            await _orchestratorProvisioner.EnsureProvisionedAsync(user.Id, ct);

            var now = DateTimeOffset.UtcNow;
            var refreshTokenString = await _tokenService.GenerateRefreshTokenAsync(ct);
            var refreshToken = new RefreshToken
            {
                Token = refreshTokenString,
                UserId = user.Id,
                FamilyId = Guid.NewGuid(),
                ExpiresAt = now.AddDays(_jwtOptions.RefreshTokenExpirationDays),
                CreatedAt = now
            };

            _db.RefreshTokens.Add(refreshToken);
            await _db.SaveChangesAsync(ct);

            return new AuthTokenResult
            {
                AccessToken = await _tokenService.GenerateAccessTokenAsync(user.Id, user.Email!, ct),
                RefreshToken = refreshTokenString,
                AccessTokenExpiresAt = now.AddMinutes(_jwtOptions.AccessTokenExpirationMinutes),
                RefreshTokenExpiresAt = now.AddDays(_jwtOptions.RefreshTokenExpirationDays)
            };
        }

        public async Task<AuthTokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;

            var existing = await _db.RefreshTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == refreshToken, ct);

            if (existing == null || existing.IsRevoked || existing.ExpiresAt < now)
                throw new UnauthorizedException("Invalid refresh token");

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            if (existing.IsUsed)
            {
                await RevokeFamilyAsync(existing.FamilyId, ct);
                await transaction.CommitAsync(ct);
                throw new UnauthorizedException("Invalid refresh token");
            }

            var user = await _userManager.FindByIdAsync(existing.UserId.ToString());
            if (user == null)
                throw new UnauthorizedException("Invalid refresh token");

            var markedUsed = await _db.RefreshTokens
                .Where(t => t.Id == existing.Id && !t.IsUsed && !t.IsRevoked)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.IsUsed, true), ct);

            if (markedUsed == 0)
            {
                await RevokeFamilyAsync(existing.FamilyId, ct);
                await transaction.CommitAsync(ct);
                throw new UnauthorizedException("Invalid refresh token");
            }

            var result = new AuthTokenResult
            {
                AccessToken = await _tokenService.GenerateAccessTokenAsync(user.Id, user.Email!, ct),
                RefreshToken = await _tokenService.GenerateRefreshTokenAsync(ct),
                AccessTokenExpiresAt = now.AddMinutes(_jwtOptions.AccessTokenExpirationMinutes),
                RefreshTokenExpiresAt = now.AddDays(_jwtOptions.RefreshTokenExpirationDays)
            };

            var newRefreshToken = new RefreshToken
            {
                FamilyId = existing.FamilyId,
                Token = result.RefreshToken,
                UserId = user.Id,
                ExpiresAt = result.RefreshTokenExpiresAt,
                CreatedAt = now,
            };

            _db.RefreshTokens.Add(newRefreshToken);
            await _db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
            return result;
        }

        public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
        {
            var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == refreshToken, ct);
            if (token == null)
                return;

            await RevokeFamilyAsync(token.FamilyId, ct);
        }


        #region Private Helpers

        private async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
        {
            await _db.RefreshTokens
                .Where(t => t.FamilyId == familyId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.IsRevoked, true), ct);
        }

        #endregion
    }
}
