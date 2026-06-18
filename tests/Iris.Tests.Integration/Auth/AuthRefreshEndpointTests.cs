using System.Net;
using FluentAssertions;
using Iris.Domain.Identity.Entities;
using Iris.Infrastructure.Identity;
using Iris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Auth;

public class AuthRefreshEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public AuthRefreshEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsNewTokenPair()
    {
        // Arrange
        var (refreshToken, familyId) = await SeedRefreshTokenAsync();
        using var client = CreateClientWithRefreshToken(refreshToken);

        // Act
        var response = await client.PostAsync(
            "/api/auth/refresh",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.Should().Contain(cookie => cookie.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase));
        cookies.Should().Contain(cookie => cookie.StartsWith("refresh_token=", StringComparison.OrdinalIgnoreCase));

        var newRefreshToken = GetCookieValue(cookies, "refresh_token");
        newRefreshToken.Should().NotBeNullOrWhiteSpace();
        newRefreshToken.Should().NotBe(refreshToken);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var oldToken = await db.RefreshTokens
            .SingleAsync(token => token.Token == refreshToken, TestContext.Current.CancellationToken);
        oldToken.IsUsed.Should().BeTrue();
        oldToken.IsRevoked.Should().BeFalse();

        var newToken = await db.RefreshTokens
            .SingleAsync(token => token.Token == newRefreshToken, TestContext.Current.CancellationToken);
        newToken.FamilyId.Should().Be(familyId);
        newToken.IsUsed.Should().BeFalse();
        newToken.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_ExpiredToken_Returns401()
    {
        // Arrange
        var (refreshToken, _) = await SeedRefreshTokenAsync(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var client = CreateClientWithRefreshToken(refreshToken);

        // Act
        var response = await client.PostAsync(
            "/api/auth/refresh",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_InvalidToken_Returns401()
    {
        // Arrange
        using var client = CreateClientWithRefreshToken($"invalid-{Guid.NewGuid()}");

        // Act
        var response = await client.PostAsync(
            "/api/auth/refresh",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_ReusedToken_RevokesFamily_Returns401()
    {
        // Arrange
        var familyId = Guid.NewGuid();
        var (refreshToken, _) = await SeedRefreshTokenAsync(familyId: familyId, isUsed: true);
        await SeedRefreshTokenAsync(familyId: familyId);

        using var client = CreateClientWithRefreshToken(refreshToken);

        // Act
        var response = await client.PostAsync(
            "/api/auth/refresh",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var familyTokens = await db.RefreshTokens
            .Where(token => token.FamilyId == familyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        familyTokens.Should().OnlyContain(token => token.IsRevoked);
    }

    [Fact]
    public async Task Refresh_AfterFamilyRevocation_NewTokenAlsoFails()
    {
        // Arrange
        var (oldRefreshToken, familyId) = await SeedRefreshTokenAsync();

        using var firstClient = CreateClientWithRefreshToken(oldRefreshToken);
        var firstResponse = await firstClient.PostAsync(
            "/api/auth/refresh",
            content: null,
            TestContext.Current.CancellationToken);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var newRefreshToken = GetCookieValue(firstResponse.Headers.GetValues("Set-Cookie"), "refresh_token");

        using var replayClient = CreateClientWithRefreshToken(oldRefreshToken);
        var replayResponse = await replayClient.PostAsync(
            "/api/auth/refresh",
            content: null,
            TestContext.Current.CancellationToken);

        replayResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var newTokenClient = CreateClientWithRefreshToken(newRefreshToken);

        // Act
        var newTokenResponse = await newTokenClient.PostAsync(
            "/api/auth/refresh",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        newTokenResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var familyTokens = await db.RefreshTokens
            .Where(token => token.FamilyId == familyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        familyTokens.Should().OnlyContain(token => token.IsRevoked);
    }

    private HttpClient CreateClientWithRefreshToken(string refreshToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });

        client.DefaultRequestHeaders.Add("Cookie", $"refresh_token={refreshToken}");
        return client;
    }

    private async Task<(string RefreshToken, Guid FamilyId)> SeedRefreshTokenAsync(
        Guid? familyId = null,
        DateTimeOffset? expiresAt = null,
        bool isUsed = false,
        bool isRevoked = false)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = new ApplicationUser
        {
            Email = $"refresh-{Guid.NewGuid()}@iris.local",
            UserName = $"refresh-{Guid.NewGuid()}@iris.local",
            DisplayName = "Refresh Test User"
        };

        var createResult = await userManager.CreateAsync(user);
        createResult.Succeeded.Should().BeTrue();

        var token = $"refresh-{Guid.NewGuid()}";
        var tokenFamilyId = familyId ?? Guid.NewGuid();

        db.RefreshTokens.Add(new RefreshToken
        {
            Token = token,
            UserId = user.Id,
            FamilyId = tokenFamilyId,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(14),
            IsUsed = isUsed,
            IsRevoked = isRevoked,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (token, tokenFamilyId);
    }

    private static string GetCookieValue(IEnumerable<string> setCookieHeaders, string cookieName)
    {
        var prefix = $"{cookieName}=";
        var cookie = setCookieHeaders.Single(header =>
            header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return Uri.UnescapeDataString(cookie[prefix.Length..].Split(';')[0]);
    }
}
