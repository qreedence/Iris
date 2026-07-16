using FluentAssertions;
using Iris.Application.Personas;
using Iris.Domain.Personas;

namespace Iris.Tests.Unit.Personas;

public class SystemPromptAssemblerTests
{
    [Fact]
    public async Task BuildAsync_AllSectionsPresent_ReturnsPromptInDefinedOrder()
    {
        // Arrange
        var sut = CreateSut("App context", "Guidelines");
        var prompt = new SystemPromptDto(
            "Identity",
            "Voice",
            "Role",
            "Relationship",
            "Tool instructions");

        // Act
        var result = await sut.BuildAsync(prompt, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(JoinSections([
            Section("app_context", "App context"),
            Section("guidelines", "Guidelines"),
            Section("identity", "Identity"),
            Section("voice", "Voice"),
            Section("role", "Role"),
            Section("relationship", "Relationship"),
            Section("tool_instructions", "Tool instructions")
        ]));
    }

    [Fact]
    public async Task BuildAsync_BlankOrNullSections_SkipsThemWithoutLiteralNullsOrEmptyHeaders()
    {
        // Arrange
        var sut = CreateSut("App context", " ");
        var prompt = new SystemPromptDto(null, "", "Role", "   ", null);

        // Act
        var result = await sut.BuildAsync(prompt, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(JoinSections([
            Section("app_context", "App context"),
            Section("role", "Role")
        ]));
        result.Should().NotContain("null");
        result.Should().NotContain("<guidelines>");
        result.Should().NotContain("<identity>");
        result.Should().NotContain("<voice>");
        result.Should().NotContain("<relationship>");
        result.Should().NotContain("<tool_instructions>");
    }

    [Fact]
    public async Task BuildAsync_SystemSectionsComeFromOptions_NotSystemPromptEntity()
    {
        // Arrange
        var sut = CreateSut("Configured app context", "Configured guidelines");
        var prompt = new SystemPromptDto(
            "Identity",
            "Voice",
            "Role",
            "Relationship",
            "Tool instructions");

        // Act
        var result = await sut.BuildAsync(prompt, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Contain("<app_context>");
        result.Should().Contain("Configured app context");
        result.Should().Contain("<guidelines>");
        result.Should().Contain("Configured guidelines");
    }

    [Fact]
    public async Task BuildAsync_PersonaHasNoSections_StillReturnsSystemSections()
    {
        // Arrange
        var sut = CreateSut("App context", "Guidelines");

        // Act
        var result = await sut.BuildAsync(SystemPromptDto.Empty, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(JoinSections([
            Section("app_context", "App context"),
            Section("guidelines", "Guidelines")
        ]));
    }

    [Fact]
    public async Task BuildAsync_SystemConfigMissingOrBlank_DoesNotThrowAndUsesPersonaSections()
    {
        // Arrange
        var sut = CreateSut(null, " ");
        var prompt = new SystemPromptDto("Identity", null, null, null, null);

        // Act
        var result = await sut.BuildAsync(prompt, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(Section("identity", "Identity"));
    }

    [Fact]
    public async Task BuildAsync_SystemPersona_UsesConfiguredOrchestratorPromptAndIgnoresEditableSections()
    {
        var sut = new SystemPromptAssembler(
            new TestGlobalSystemPromptProvider("App context", "Guidelines", "Onboarding instructions"));
        var persona = CreatePersona(
            PersonaKind.System,
            new SystemPromptDto("Must not leak", "Voice", "Role", "Relationship", "Tools"));

        var result = await sut.BuildAsync(persona, TestContext.Current.CancellationToken);

        result.Should().Be(JoinSections([
            Section("app_context", "App context"),
            Section("guidelines", "Guidelines"),
            Section("orchestrator", "Onboarding instructions")
        ]));
        result.Should().NotContain("Must not leak");
        result.Should().NotContain("<identity>");
    }

    private static SystemPromptAssembler CreateSut(string? appContext, string? guidelines)
    {
        return new SystemPromptAssembler(new TestGlobalSystemPromptProvider(appContext, guidelines));
    }

    private static string JoinSections(IEnumerable<string> sections)
    {
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", sections);
    }

    private static string Section(string tagName, string content)
    {
        return $"<{tagName}>{Environment.NewLine}{content}{Environment.NewLine}</{tagName}>";
    }

    private class TestGlobalSystemPromptProvider : IGlobalSystemPromptProvider
    {
        private readonly GlobalSystemPromptSections _sections;

        public TestGlobalSystemPromptProvider(
            string? appContext,
            string? guidelines,
            string? orchestrator = null)
        {
            _sections = new GlobalSystemPromptSections(appContext, guidelines, orchestrator);
        }

        public Task<GlobalSystemPromptSections> GetAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_sections);
        }
    }

    private static PersonaDto CreatePersona(PersonaKind kind, SystemPromptDto prompt) => new(
        Guid.NewGuid(),
        "Iris",
        prompt,
        null,
        "orchestrator",
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        kind);
}
