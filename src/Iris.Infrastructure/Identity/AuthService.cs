using Iris.Application.Exceptions;
using Iris.Application.Identity.DTOs;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.Identity.Entities;
using Iris.Domain.Identity.Enums;
using Iris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Iris.Infrastructure.Identity
{
    public class AuthService : IAuthService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly JwtOptions _jwtOptions;
        private readonly AppDbContext _db;

        public AuthService(UserManager<ApplicationUser> userManager, IOptions<JwtOptions> jwtOptions, AppDbContext db)
        {
            _userManager = userManager;
            _jwtOptions = jwtOptions.Value;
            _db = db;
        }

        public async Task<AuthTokenResult> HandleSocialLoginAsync(LoginProvider provider, ClaimsPrincipal claims, CancellationToken ct = default)
        {
            var email = claims.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrEmpty(email))
                throw new ValidationException("Invalid email");

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

                var providerUserId = claims.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrWhiteSpace(providerUserId))
                    throw new ValidationException("Invalid provider user id");

                var loginInfo = new UserLoginInfo(provider.ToString(), providerUserId, provider.ToString());
                await _userManager.AddLoginAsync(user, loginInfo);
            }

            var refreshTokenString = GenerateRefreshToken();
            var refreshToken = new RefreshToken
            {
                Token = refreshTokenString,
                UserId = user.Id,
                FamilyId = Guid.NewGuid(),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenExpirationDays),
                CreatedAt = DateTimeOffset.UtcNow
            };

            _db.RefreshTokens.Add(refreshToken);
            await _db.SaveChangesAsync(ct);

            return new AuthTokenResult 
            {
                AccessToken = GenerateAccessToken(user),
                RefreshToken = refreshTokenString,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenExpirationMinutes),
                RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenExpirationDays)
            };
        }

        #region Private Helpers

        private string GenerateAccessToken(ApplicationUser user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Secret));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email!)
            };

            var token = new JwtSecurityToken(
                issuer: _jwtOptions.Issuer,
                audience: _jwtOptions.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_jwtOptions.AccessTokenExpirationMinutes),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private string GenerateRefreshToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(64);
            return Convert.ToBase64String(bytes);
        }

        #endregion
    }
}
