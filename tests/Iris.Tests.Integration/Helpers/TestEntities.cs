using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Helpers;

/// <summary>
/// Creates a persona via direct service calls (bypassing HTTP). Used by command-flow,
/// projector, tenant-isolation, and worker tests that dispatch MediatR commands
/// directly rather than through an HttpClient.
/// </summary>
public static class TestPersonas
{
    /// <summary>
    /// Creates a persona owned by <paramref name="userId"/> using a fresh DI scope
    /// from <paramref name="services"/>.
    /// </summary>
    public static async Task<PersonaDto> CreateAsync(
        IServiceProvider services,
        Guid userId,
        string name = "Iris",
        SystemPromptSectionsRequest? systemPrompt = null,
        string? modelPreference = null,
        CancellationToken ct = default,
        string? role = null)
    {
        using var scope = services.CreateScope();
        var personaService = scope.ServiceProvider.GetRequiredService<IPersonaService>();
        return await personaService.CreateAsync(
            userId,
            new CreatePersonaRequest(name, systemPrompt, ModelPreference: modelPreference, Role: role),
            ct);
    }
}

/// <summary>
/// Creates a persona via the HTTP client, asserting the 201 Created contract along
/// the way. Used by endpoint tests that exercise the API surface directly.
/// </summary>
public static class TestPersonaClient
{
    public static async Task<PersonaDto> CreatePersonaAsync(
        HttpClient client,
        string name = "Iris",
        SystemPromptSectionsRequest? systemPrompt = null,
        string? modelPreference = null,
        CancellationToken ct = default)
    {
        var response = await client.PostAsJsonAsync(
            "/api/personas",
            new CreatePersonaRequest(name, systemPrompt, ModelPreference: modelPreference),
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var persona = await response.Content.ReadFromJsonAsync<PersonaDto>(TestJson.Options, ct);
        return persona!;
    }
}

/// <summary>
/// Creates conversations via direct MediatR dispatch (bypassing HTTP). Used by
/// command-flow, projector, tenant-isolation, and worker tests.
/// </summary>
public static class TestConversations
{
    /// <summary>
    /// Creates a conversation owned by <paramref name="userId"/> for the given
    /// persona, dispatching <see cref="CreateConversationCommand"/> in a fresh scope.
    /// Returns the generated conversation id.
    /// </summary>
    public static async Task<Guid> CreateAsync(
        IServiceProvider services,
        Guid userId,
        Guid personaId,
        string title = "Chat",
        CancellationToken ct = default)
    {
        var conversationId = Guid.NewGuid();
        await services.SendCommandAsAsync(
            userId,
            new CreateConversationCommand(conversationId, userId, personaId, title),
            ct);
        return conversationId;
    }
}
