using System.Net;
using FluentAssertions;
using Iris.Domain.Identity.Entities;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Auth;

public class AuthLogoutEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public AuthLogoutEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Logout_WithoutRefreshCookie_Returns200AndClearsCookies()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsync(
            "/api/auth/logout",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase));
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.StartsWith("refresh_token=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logout_WithUnknownRefreshToken_Returns200AndClearsCookies()
    {
        // Arrange
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "refresh_token=unknown");

        // Act
        var response = await client.PostAsync(
            "/api/auth/logout",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase));
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.StartsWith("refresh_token=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logout_WithValidRefreshToken_RevokesFamilyAndClearsCookies()
    {
        // Arrange
        var familyId = Guid.NewGuid();
        var refreshToken = $"refresh-{Guid.NewGuid()}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RefreshTokens.AddRange(
                new RefreshToken
                {
                    Token = refreshToken,
                    UserId = Guid.NewGuid(),
                    FamilyId = familyId,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new RefreshToken
                {
                    Token = $"refresh-{Guid.NewGuid()}",
                    UserId = Guid.NewGuid(),
                    FamilyId = familyId,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"refresh_token={refreshToken}");

        // Act
        var response = await client.PostAsync(
            "/api/auth/logout",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase));
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.StartsWith("refresh_token=", StringComparison.OrdinalIgnoreCase));

        using var assertScope = _factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var familyTokens = await assertDb.RefreshTokens
            .Where(token => token.FamilyId == familyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        familyTokens.Should().OnlyContain(token => token.IsRevoked);
    }
}
