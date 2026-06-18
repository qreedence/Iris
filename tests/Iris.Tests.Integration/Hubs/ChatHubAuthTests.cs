using System.Net;
using FluentAssertions;

namespace Iris.Tests.Integration.Hubs;

public class ChatHubAuthTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public ChatHubAuthTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ChatHub_NegotiateWithoutAuth_Returns401()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsync(
            "/hubs/chat/negotiate?negotiateVersion=1",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChatHub_NegotiateWithAuthenticatedUser_Returns200()
    {
        // Arrange
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        // Act
        var response = await client.PostAsync(
            "/hubs/chat/negotiate?negotiateVersion=1",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChatHub_NegotiateWithQueryStringToken_Returns200()
    {
        // Arrange
        using var client = _factory.CreateClient();
        var userId = Guid.NewGuid();

        // Act
        var response = await client.PostAsync(
            $"/hubs/chat/negotiate?negotiateVersion=1&access_token={userId}",
            content: null,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
