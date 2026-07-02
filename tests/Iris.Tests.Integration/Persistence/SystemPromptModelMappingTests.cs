using FluentAssertions;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Persistence;

/// <summary>
/// EF model-mapping assertions for SystemPrompt — never touches the HTTP client, so
/// it lives alongside the other direct-DbContext persistence tests rather than in the
/// Persona endpoint test files.
/// </summary>
[Collection("ApiTestFactory collection")]
public class SystemPromptModelMappingTests
{
    private readonly ApiTestFactory _factory;

    public SystemPromptModelMappingTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void SystemPromptModel_UsesPersonaIdAsRequiredOneToOneKeyAndHasNoPlatformSections()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Act
        var entityType = db.Model.FindEntityType(typeof(SystemPrompt));

        // Assert
        entityType.Should().NotBeNull();
        entityType!.FindPrimaryKey()!.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(SystemPrompt.PersonaId));
        entityType.FindProperty("AppContext").Should().BeNull();
        entityType.FindProperty("Guidelines").Should().BeNull();
    }
}
