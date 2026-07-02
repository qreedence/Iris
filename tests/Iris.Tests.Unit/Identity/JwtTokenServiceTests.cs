using FluentAssertions;
using Iris.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;

namespace Iris.Tests.Unit.Identity;

public class JwtTokenServiceTests
{
    private static JwtTokenService CreateSut(int accessTokenExpirationMinutes = 15)
    {
        return new JwtTokenService(Options.Create(new JwtOptions
        {
            Secret = "test-jwt-secret-with-enough-length-for-hmac-sha256",
            Issuer = "Iris.Api.Tests",
            Audience = "Iris.Client.Tests",
            AccessTokenExpirationMinutes = accessTokenExpirationMinutes,
            RefreshTokenExpirationDays = 14
        }));
    }

    [Fact]
    public async Task GenerateAccessTokenAsync_WithValidUser_ReturnsTokenWithCorrectClaims()
    {
        // Arrange
        var sut = CreateSut();
        var userId = Guid.NewGuid();
        var email = "test@iris.local";

        // Act
        var tokenString = await sut.GenerateAccessTokenAsync(
            userId,
            email,
            TestContext.Current.CancellationToken);

        // Assert
        var token = new JwtSecurityTokenHandler().ReadJwtToken(tokenString);

        token.Issuer.Should().Be("Iris.Api.Tests");
        token.Audiences.Should().ContainSingle().Which.Should().Be("Iris.Client.Tests");
        token.Claims.Should().Contain(claim => claim.Type == JwtRegisteredClaimNames.Sub && claim.Value == userId.ToString());
        token.Claims.Should().Contain(claim => claim.Type == JwtRegisteredClaimNames.Email && claim.Value == email);
    }

    [Fact]
    public async Task GenerateAccessTokenAsync_TokenExpiresInConfiguredMinutes()
    {
        // Arrange
        var sut = CreateSut(accessTokenExpirationMinutes: 15);
        var before = DateTime.UtcNow;

        // Act
        var tokenString = await sut.GenerateAccessTokenAsync(
            Guid.NewGuid(),
            "test@iris.local",
            TestContext.Current.CancellationToken);
        var after = DateTime.UtcNow;

        // Assert
        var token = new JwtSecurityTokenHandler().ReadJwtToken(tokenString);
        token.ValidTo.Should().BeOnOrAfter(before.AddMinutes(15).AddSeconds(-1));
        token.ValidTo.Should().BeOnOrBefore(after.AddMinutes(15).AddSeconds(1));
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_ReturnsSecureRandomToken()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var token = await sut.GenerateRefreshTokenAsync(TestContext.Current.CancellationToken);

        // Assert
        token.Should().NotBeNullOrWhiteSpace();
        Convert.FromBase64String(token).Should().HaveCount(64);
    }

    [Fact]
    public async Task GenerateRefreshTokenAsync_ConsecutiveCallsProduceDifferentTokens()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var first = await sut.GenerateRefreshTokenAsync(TestContext.Current.CancellationToken);
        var second = await sut.GenerateRefreshTokenAsync(TestContext.Current.CancellationToken);

        // Assert
        second.Should().NotBe(first);
    }
}
