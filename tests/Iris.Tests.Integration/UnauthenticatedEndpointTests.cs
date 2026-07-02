using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Iris.Tests.Integration;

/// <summary>
/// Every protected REST endpoint must reject unauthenticated requests with 401. This
/// collapses what used to be ~10 copy-pasted `*_WithoutAuth_Returns401` facts spread
/// across PersonaEndpointTests, ConversationEndpointTests, and ChatEndpointTests into
/// one matrix covering every [Authorize]'d route on PersonasController and
/// ConversationsController — including routes that never had 401 coverage before
/// (persona GetById, system-prompt section PUT/DELETE, and the /chat/cancel endpoint).
/// </summary>
public class UnauthenticatedEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public UnauthenticatedEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string, object?> ProtectedEndpoints => new()
    {
        // ── Personas ────────────────────────────────────────────────
        { "POST", "/api/personas", new { name = "Nope" } },
        { "GET", "/api/personas", null },
        { "GET", $"/api/personas/{Guid.NewGuid()}", null },
        { "PUT", $"/api/personas/{Guid.NewGuid()}", new { name = "Nope" } },
        { "GET", $"/api/personas/{Guid.NewGuid()}/system-prompt", null },
        { "PUT", $"/api/personas/{Guid.NewGuid()}/system-prompt", new { identity = "Nope" } },
        { "PUT", $"/api/personas/{Guid.NewGuid()}/system-prompt/sections/identity", new { content = "Nope" } },
        { "DELETE", $"/api/personas/{Guid.NewGuid()}/system-prompt/sections/identity", null },
        { "DELETE", $"/api/personas/{Guid.NewGuid()}", null },

        // ── Conversations ───────────────────────────────────────────
        { "POST", "/api/conversations", new { personaId = Guid.NewGuid(), title = "Nope" } },
        { "GET", "/api/conversations", null },
        { "GET", $"/api/conversations/{Guid.NewGuid()}/messages", null },
        { "POST", $"/api/conversations/{Guid.NewGuid()}/chat", new { userMessage = "Nope", model = "test/model" } },
        { "POST", $"/api/conversations/{Guid.NewGuid()}/chat/cancel", null },
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task ProtectedEndpoint_WithoutAuth_Returns401(string method, string route, object? body)
    {
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
