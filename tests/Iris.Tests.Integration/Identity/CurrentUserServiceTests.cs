using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Iris.Api.Authentication;
using Iris.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Iris.Tests.Integration.Identity;

public class CurrentUserServiceTests
{
    private static CurrentUserService CreateSut(ClaimsPrincipal? user = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();

        if (user != null)
        {
            var httpContext = new DefaultHttpContext { User = user };
            accessor.HttpContext.Returns(httpContext);
        }
        else
        {
            accessor.HttpContext.Returns((HttpContext?)null);
        }

        return new CurrentUserService(accessor);
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "Test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void UserId_WithValidSubClaim_ReturnsParsedGuid()
    {
        var expected = Guid.NewGuid();
        var sut = CreateSut(CreatePrincipal(
            new Claim(JwtRegisteredClaimNames.Sub, expected.ToString())));

        sut.UserId.Should().Be(expected);
    }

    [Fact]
    public void UserId_WhenSubClaimMissing_ThrowsUnauthorizedException()
    {
        var sut = CreateSut(CreatePrincipal(
            new Claim(JwtRegisteredClaimNames.Email, "test@test.com")));

        var act = () => sut.UserId;

        act.Should().Throw<UnauthorizedException>();
    }

    [Fact]
    public void UserId_WhenSubClaimMalformed_ThrowsUnauthorizedException()
    {
        var sut = CreateSut(CreatePrincipal(
            new Claim(JwtRegisteredClaimNames.Sub, "not-a-guid")));

        var act = () => sut.UserId;

        act.Should().Throw<UnauthorizedException>();
    }

    [Fact]
    public void UserId_WhenNoHttpContext_ReturnsEmptyGuid()
    {
        var sut = CreateSut(user: null);

        sut.UserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void UserId_WithOverride_ReturnsOverrideRegardlessOfHttpContext()
    {
        var httpUserId = Guid.NewGuid();
        var overrideUserId = Guid.NewGuid();

        var sut = CreateSut(CreatePrincipal(
            new Claim(JwtRegisteredClaimNames.Sub, httpUserId.ToString())));
        sut.OverrideUserId = overrideUserId;

        sut.UserId.Should().Be(overrideUserId);
    }

    [Fact]
    public void UserId_WithOverride_DoesNotFallBackToHttpContext()
    {
        var overrideUserId = Guid.NewGuid();
        var sut = CreateSut(user: null);
        sut.OverrideUserId = overrideUserId;

        sut.UserId.Should().Be(overrideUserId);
    }

    [Fact]
    public void IsAuthenticated_WithAuthenticatedUser_ReturnsTrue()
    {
        var sut = CreateSut(CreatePrincipal(
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())));

        sut.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void IsAuthenticated_WhenNoHttpContext_ReturnsFalse()
    {
        var sut = CreateSut(user: null);

        sut.IsAuthenticated.Should().BeFalse();
    }
}
