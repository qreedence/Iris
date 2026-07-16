using System.Security.Claims;
using FluentAssertions;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.Identity.Enums;
using Iris.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Iris.Infrastructure.Persistence;
using Iris.Domain.Personas;
using Microsoft.EntityFrameworkCore;

namespace Iris.Tests.Integration.Auth;

[Collection("ApiTestFactory collection")]
public class AuthServiceAccountLinkingTests
{
    private readonly ApiTestFactory _factory;

    public AuthServiceAccountLinkingTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HandleSocialLoginAsync_NewUser_AddsProviderLogin()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"new-{Guid.NewGuid()}@iris.local";
        var providerUserId = $"google-{Guid.NewGuid()}";

        // Act
        await authService.HandleSocialLoginAsync(
            LoginProvider.Google,
            CreatePrincipal(email, providerUserId),
            TestContext.Current.CancellationToken);

        // Assert
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();

        var logins = await userManager.GetLoginsAsync(user!);
        logins.Should().ContainSingle(login =>
            login.LoginProvider == LoginProvider.Google.ToString()
            && login.ProviderKey == providerUserId);
    }

    [Fact]
    public async Task HandleSocialLoginAsync_NewThenReturningUser_ProvisionsOrchestratorExactlyOnce()
    {
        using var scope = _factory.Services.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"provision-{Guid.NewGuid()}@iris.local";
        var principal = CreatePrincipal(email, $"google-{Guid.NewGuid()}");

        await authService.HandleSocialLoginAsync(
            LoginProvider.Google,
            principal,
            TestContext.Current.CancellationToken);
        await authService.HandleSocialLoginAsync(
            LoginProvider.Google,
            principal,
            TestContext.Current.CancellationToken);

        var user = await userManager.FindByEmailAsync(email);
        currentUser.OverrideUserId = user!.Id;
        var orchestrators = await db.Personas
            .Where(persona => persona.Kind == PersonaKind.System)
            .ToListAsync(TestContext.Current.CancellationToken);
        orchestrators.Should().ContainSingle();
        (await db.ConversationReadModels.CountAsync(
            conversation => conversation.PersonaId == orchestrators[0].Id,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task HandleSocialLoginAsync_ExistingEmailWithoutProvider_AddsProviderLogin()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"existing-{Guid.NewGuid()}@iris.local";
        var providerUserId = $"google-{Guid.NewGuid()}";
        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = "Existing User"
        };

        var createResult = await userManager.CreateAsync(user);
        createResult.Succeeded.Should().BeTrue();

        // Act
        await authService.HandleSocialLoginAsync(
            LoginProvider.Google,
            CreatePrincipal(email, providerUserId),
            TestContext.Current.CancellationToken);

        // Assert
        var logins = await userManager.GetLoginsAsync(user);
        logins.Should().ContainSingle(login =>
            login.LoginProvider == LoginProvider.Google.ToString()
            && login.ProviderKey == providerUserId);
    }

    [Fact]
    public async Task HandleSocialLoginAsync_ExistingProviderLogin_DoesNotAddDuplicateLogin()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"linked-{Guid.NewGuid()}@iris.local";
        var providerUserId = $"google-{Guid.NewGuid()}";
        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = "Linked User"
        };

        var createResult = await userManager.CreateAsync(user);
        createResult.Succeeded.Should().BeTrue();

        var addLoginResult = await userManager.AddLoginAsync(
            user,
            new UserLoginInfo(LoginProvider.Google.ToString(), providerUserId, LoginProvider.Google.ToString()));
        addLoginResult.Succeeded.Should().BeTrue();

        // Act
        await authService.HandleSocialLoginAsync(
            LoginProvider.Google,
            CreatePrincipal(email, providerUserId),
            TestContext.Current.CancellationToken);

        // Assert
        var logins = await userManager.GetLoginsAsync(user);
        logins.Should().ContainSingle(login =>
            login.LoginProvider == LoginProvider.Google.ToString()
            && login.ProviderKey == providerUserId);
    }

    private static ClaimsPrincipal CreatePrincipal(string email, string providerUserId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.NameIdentifier, providerUserId),
            new Claim(ClaimTypes.Name, "Iris User")
        }, LoginProvider.Google.ToString());

        return new ClaimsPrincipal(identity);
    }
}
