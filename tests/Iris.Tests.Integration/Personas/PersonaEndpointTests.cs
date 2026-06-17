using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Iris.Application.Personas;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Personas;

public class PersonaEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly HttpClient _client;

    public PersonaEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(_userId);
    }

    [Fact]
    public async Task PostPersona_ValidData_Returns201WithDto()
    {
        // Arrange
        var request = new CreatePersonaRequest("Iris", "Be concise.");

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/personas",
            request,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var persona = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        persona.Should().NotBeNull();
        persona!.Id.Should().NotBe(Guid.Empty);
        persona.Name.Should().Be("Iris");
        persona.SystemPrompt.Should().Be("Be concise.");
        persona.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        persona.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task PostPersona_EmptyName_Returns400()
    {
        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/personas",
            new CreatePersonaRequest(""),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetPersonas_ReturnsPersonasForAuthenticatedUser()
    {
        // Arrange
        var personaA1 = await CreatePersonaAsync("A One");
        var personaA2 = await CreatePersonaAsync("A Two");
        await CreatePersonaForUserAsync(Guid.NewGuid(), "B One");

        // Act
        var response = await _client.GetAsync(
            "/api/personas",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var personas = await response.Content.ReadFromJsonAsync<List<PersonaDto>>(
            cancellationToken: TestContext.Current.CancellationToken);

        personas.Should().NotBeNull();
        personas!.Select(p => p.Id).Should().Contain([personaA1.Id, personaA2.Id]);
        personas.Should().OnlyContain(p => p.Id == personaA1.Id || p.Id == personaA2.Id);
    }

    [Fact]
    public async Task GetPersona_Exists_Returns200()
    {
        // Arrange
        var created = await CreatePersonaAsync("Iris", "Be useful.");

        // Act
        var response = await _client.GetAsync(
            $"/api/personas/{created.Id}",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var persona = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        persona.Should().NotBeNull();
        persona!.Id.Should().Be(created.Id);
        persona.Name.Should().Be("Iris");
        persona.SystemPrompt.Should().Be("Be useful.");
    }

    [Fact]
    public async Task GetPersona_NotFound_Returns404()
    {
        // Act
        var response = await _client.GetAsync(
            $"/api/personas/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPersona_OtherUsersPersona_Returns404()
    {
        // Arrange
        var otherPersona = await CreatePersonaForUserAsync(Guid.NewGuid(), "Other User Persona");

        // Act
        var response = await _client.GetAsync(
            $"/api/personas/{otherPersona.Id}",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutPersona_ValidData_Returns200WithUpdatedFields()
    {
        // Arrange
        var created = await CreatePersonaAsync("Before");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}",
            new UpdatePersonaRequest("After", SystemPrompt: "Updated prompt.", ModelPreference: "test/model", Avatar: "https://example.com/avatar.png"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        updated.Should().NotBeNull();
        updated!.Id.Should().Be(created.Id);
        updated.Name.Should().Be("After");
        updated.SystemPrompt.Should().Be("Updated prompt.");
        updated.ModelPreference.Should().Be("test/model");
        updated.Avatar.Should().Be("https://example.com/avatar.png");
        updated.UpdatedAt.Should().BeOnOrAfter(created.UpdatedAt);
    }

    [Fact]
    public async Task PutPersona_OtherUsersPersona_Returns404()
    {
        // Arrange
        var otherPersona = await CreatePersonaForUserAsync(Guid.NewGuid(), "Other User Persona");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{otherPersona.Id}",
            new UpdatePersonaRequest("Nope"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeletePersona_Exists_Returns204()
    {
        // Arrange
        var created = await CreatePersonaAsync("Delete Me");

        // Act
        var deleteResponse = await _client.DeleteAsync(
            $"/api/personas/{created.Id}",
            TestContext.Current.CancellationToken);
        var getResponse = await _client.GetAsync(
            $"/api/personas/{created.Id}",
            TestContext.Current.CancellationToken);

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeletePersona_NotFound_Returns404()
    {
        // Act
        var response = await _client.DeleteAsync(
            $"/api/personas/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeletePersona_OtherUsersPersona_Returns404()
    {
        // Arrange
        var otherPersona = await CreatePersonaForUserAsync(Guid.NewGuid(), "Other User Persona");

        // Act
        var response = await _client.DeleteAsync(
            $"/api/personas/{otherPersona.Id}",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostPersona_WithRoleAndGroup_Returns201WithFields()
    {
        // Arrange
        var request = new CreatePersonaRequest("Iris", Role: "Backend Architect", Group: "Dev Team");

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/personas",
            request,
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var persona = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        persona!.Role.Should().Be("Backend Architect");
        persona.Group.Should().Be("Dev Team");
    }

    [Fact]
    public async Task PostPersona_WithoutRoleAndGroup_FieldsAreNull()
    {
        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/personas",
            new CreatePersonaRequest("Iris"),
            TestContext.Current.CancellationToken);

        // Assert
        var persona = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        persona!.Role.Should().BeNull();
        persona.Group.Should().BeNull();
    }

    [Fact]
    public async Task PutPersona_UpdatesRoleAndGroup()
    {
        // Arrange
        var created = await CreatePersonaAsync("Iris");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}",
            new UpdatePersonaRequest("Iris", Role: "QA Engineer", Group: "Testing"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        updated!.Role.Should().Be("QA Engineer");
        updated.Group.Should().Be("Testing");
    }

    private async Task<PersonaDto> CreatePersonaAsync(
        string name,
        string? systemPrompt = null,
        string? modelPreference = null)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/personas",
            new CreatePersonaRequest(name, systemPrompt, modelPreference),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var persona = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        return persona!;
    }

    private async Task<PersonaDto> CreatePersonaForUserAsync(
        Guid userId,
        string name)
    {
        using var scope = _factory.Services.CreateScope();
        var personaService = scope.ServiceProvider.GetRequiredService<IPersonaService>();

        return await personaService.CreateAsync(
            userId,
            new CreatePersonaRequest(name),
            TestContext.Current.CancellationToken);
    }
}
