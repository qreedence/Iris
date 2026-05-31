using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Iris.Application.Personas;

namespace Iris.Tests.Integration.Personas;

public class PersonaEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public PersonaEndpointTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostPersona_ValidData_Returns201WithDto()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new CreatePersonaRequest(userId, "Iris", "Be concise.");

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
            new CreatePersonaRequest(Guid.NewGuid(), ""),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetPersonas_ReturnsPersonasForUser()
    {
        // Arrange
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var personaA1 = await CreatePersonaAsync(userA, "A One");
        var personaA2 = await CreatePersonaAsync(userA, "A Two");
        await CreatePersonaAsync(userB, "B One");

        // Act
        var response = await _client.GetAsync(
            $"/api/personas?userId={userA}",
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
        var userId = Guid.NewGuid();
        var created = await CreatePersonaAsync(userId, "Iris", "Be useful.");

        // Act
        var response = await _client.GetAsync(
            $"/api/personas/{created.Id}?userId={userId}",
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
            $"/api/personas/{Guid.NewGuid()}?userId={Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutPersona_ValidData_Returns200WithUpdatedFields()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var created = await CreatePersonaAsync(userId, "Before");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}?userId={userId}",
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
    public async Task DeletePersona_Exists_Returns204()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var created = await CreatePersonaAsync(userId, "Delete Me");

        // Act
        var deleteResponse = await _client.DeleteAsync(
            $"/api/personas/{created.Id}?userId={userId}",
            TestContext.Current.CancellationToken);
        var getResponse = await _client.GetAsync(
            $"/api/personas/{created.Id}?userId={userId}",
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
            $"/api/personas/{Guid.NewGuid()}?userId={Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostPersona_WithRoleAndGroup_Returns201WithFields()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new CreatePersonaRequest(userId, "Iris", Role: "Backend Architect", Group: "Dev Team");

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
        // Arrange
        var userId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsJsonAsync(
            "/api/personas",
            new CreatePersonaRequest(userId, "Iris"),
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
        var userId = Guid.NewGuid();
        var created = await CreatePersonaAsync(userId, "Iris");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/api/personas/{created.Id}?userId={userId}",
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
        Guid userId,
        string name,
        string? systemPrompt = null,
        string? modelPreference = null)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/personas",
            new CreatePersonaRequest(userId, name, systemPrompt, modelPreference),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var persona = await response.Content.ReadFromJsonAsync<PersonaDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        return persona!;
    }
}
