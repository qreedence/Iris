using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Iris.Api.Authentication;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Personas;

[Collection("ApiTestFactory collection")]
public class PersonaSystemPromptEndpointTests
{
    private readonly ApiTestFactory _factory;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly HttpClient _client;

    public PersonaSystemPromptEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(_userId);
    }

    [Fact]
    public async Task GetSystemPrompt_ReturnsOwnPersonaPrompt()
    {
        // Arrange
        var created = await CreatePersonaAsync(
            "Iris",
            new SystemPromptSectionsRequest(
                Identity: "I am Iris.",
                Voice: "Warm.",
                Role: "Help the user."));

        // Act
        var response = await _client.GetAsync(
            $"/api/personas/{created.Id}/system-prompt",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prompt = await response.Content.ReadFromJsonAsync<SystemPromptDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        prompt.Should().NotBeNull();
        prompt!.Identity.Should().Be("I am Iris.");
        prompt.Voice.Should().Be("Warm.");
        prompt.Role.Should().Be("Help the user.");
    }

    [Fact]
    public async Task GetSystemPrompt_OtherUsersPersona_Returns404()
    {
        // Arrange
        var otherPersona = await CreatePersonaForUserAsync(Guid.NewGuid(), "Other User Persona");

        // Act
        var response = await _client.GetAsync(
            $"/api/personas/{otherPersona.Id}/system-prompt",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutSystemPrompt_OtherUsersPersona_Returns404AndDoesNotModifyPrompt()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        var originalPrompt = new SystemPromptSectionsRequest(
            Identity: "Other identity",
            Voice: "Other voice",
            Role: "Other role",
            Relationship: "Other relationship",
            ToolInstructions: "Other tools");
        var otherPersona = await CreatePersonaForUserAsync(otherUserId, "Other User Persona", originalPrompt);

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{otherPersona.Id}/system-prompt",
            new SystemPromptSectionsRequest(Identity: "Nope"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unchanged = await GetSystemPromptDirectAsync(otherUserId, otherPersona.Id);
        unchanged.Should().Be(new SystemPromptDto(
            "Other identity",
            "Other voice",
            "Other role",
            "Other relationship",
            "Other tools"));
    }

    [Fact]
    public async Task PutSystemPromptSection_OtherUsersPersona_Returns404AndDoesNotModifyPrompt()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        var otherPersona = await CreatePersonaForUserAsync(
            otherUserId,
            "Other User Persona",
            new SystemPromptSectionsRequest(Identity: "Other identity", Voice: "Other voice"));

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{otherPersona.Id}/system-prompt/sections/voice",
            new UpdateSystemPromptSectionRequest("Nope"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unchanged = await GetSystemPromptDirectAsync(otherUserId, otherPersona.Id);
        unchanged.Should().Be(new SystemPromptDto(
            "Other identity",
            "Other voice",
            null,
            null,
            null));
    }

    [Fact]
    public async Task DeleteSystemPromptSection_OtherUsersPersona_Returns404AndDoesNotModifyPrompt()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        var otherPersona = await CreatePersonaForUserAsync(
            otherUserId,
            "Other User Persona",
            new SystemPromptSectionsRequest(Identity: "Other identity", Voice: "Other voice"));

        // Act
        var response = await _client.DeleteAsync(
            $"/api/personas/{otherPersona.Id}/system-prompt/sections/identity",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unchanged = await GetSystemPromptDirectAsync(otherUserId, otherPersona.Id);
        unchanged.Should().Be(new SystemPromptDto(
            "Other identity",
            "Other voice",
            null,
            null,
            null));
    }

    [Fact]
    public async Task PutSystemPrompt_UpdatesAllEditableSections()
    {
        // Arrange
        var created = await CreatePersonaAsync("Iris");
        var request = new SystemPromptSectionsRequest(
            Identity: "Identity",
            Voice: "Voice",
            Role: "Role",
            Relationship: "Relationship",
            ToolInstructions: "Tools");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}/system-prompt",
            request,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prompt = await response.Content.ReadFromJsonAsync<SystemPromptDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        prompt.Should().Be(new SystemPromptDto(
            "Identity",
            "Voice",
            "Role",
            "Relationship",
            "Tools"));
    }

    [Fact]
    public async Task PutSystemPromptSection_UpdatesOneSectionAndPreservesOthers()
    {
        // Arrange
        var created = await CreatePersonaAsync(
            "Iris",
            new SystemPromptSectionsRequest(
                Identity: "Identity",
                Voice: "Voice",
                Role: "Role",
                Relationship: "Relationship",
                ToolInstructions: "Tools"));

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}/system-prompt/sections/voice",
            new UpdateSystemPromptSectionRequest("New voice"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prompt = await response.Content.ReadFromJsonAsync<SystemPromptDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        prompt.Should().Be(new SystemPromptDto(
            "Identity",
            "New voice",
            "Role",
            "Relationship",
            "Tools"));
    }

    [Fact]
    public async Task DeleteSystemPromptSection_ClearsSection()
    {
        // Arrange
        var created = await CreatePersonaAsync(
            "Iris",
            new SystemPromptSectionsRequest(Identity: "Identity", Voice: "Voice"));

        // Act
        var response = await _client.DeleteAsync(
            $"/api/personas/{created.Id}/system-prompt/sections/identity",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prompt = await response.Content.ReadFromJsonAsync<SystemPromptDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        prompt!.Identity.Should().BeNull();
        prompt.Voice.Should().Be("Voice");
    }

    [Fact]
    public async Task PutSystemPromptSection_WhitespaceContent_ClearsSection()
    {
        // Arrange
        var created = await CreatePersonaAsync(
            "Iris",
            new SystemPromptSectionsRequest(Identity: "Identity", Voice: "Voice"));

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}/system-prompt/sections/identity",
            new UpdateSystemPromptSectionRequest("   "),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var prompt = await response.Content.ReadFromJsonAsync<SystemPromptDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        prompt!.Identity.Should().BeNull();
        prompt.Voice.Should().Be("Voice");
    }

    [Fact]
    public async Task PutSystemPromptSection_InvalidSection_Returns400()
    {
        // Arrange
        var created = await CreatePersonaAsync("Iris");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}/system-prompt/sections/app-context",
            new UpdateSystemPromptSectionRequest("Nope"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutSystemPrompt_PlatformOwnedSection_Returns400()
    {
        // Arrange
        var created = await CreatePersonaAsync("Iris");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}/system-prompt",
            new { appContext = "Nope" },
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutSystemPrompt_MissingPersona_Returns404()
    {
        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{Guid.NewGuid()}/system-prompt",
            new SystemPromptSectionsRequest(Identity: "Nope"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSystemPrompt_SoftDeletedPersona_Returns404()
    {
        // Arrange
        var created = await CreatePersonaAsync("Iris");
        await _client.DeleteAsync(
            $"/api/personas/{created.Id}",
            TestContext.Current.CancellationToken);

        // Act
        var response = await _client.GetAsync(
            $"/api/personas/{created.Id}/system-prompt",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task<PersonaDto> CreatePersonaAsync(
        string name,
        SystemPromptSectionsRequest? systemPrompt = null) =>
        TestPersonaClient.CreatePersonaAsync(
            _client, name, systemPrompt, ct: TestContext.Current.CancellationToken);

    private Task<PersonaDto> CreatePersonaForUserAsync(
        Guid userId,
        string name,
        SystemPromptSectionsRequest? systemPrompt = null) =>
        TestPersonas.CreateAsync(
            _factory.Services, userId, name, systemPrompt, ct: TestContext.Current.CancellationToken);

    private async Task<SystemPromptDto> GetSystemPromptDirectAsync(Guid userId, Guid personaId)
    {
        using var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = userId;
        var systemPromptService = scope.ServiceProvider.GetRequiredService<ISystemPromptService>();
        return await systemPromptService.GetByPersonaIdAsync(personaId, TestContext.Current.CancellationToken);
    }
}
