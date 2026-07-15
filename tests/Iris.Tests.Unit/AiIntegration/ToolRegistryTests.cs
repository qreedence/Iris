using System.Text.Json;
using FluentAssertions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Infrastructure.AiIntegration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iris.Tests.Unit.AiIntegration;

public class ToolRegistryTests
{
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();
    private readonly Guid _personaId = Guid.NewGuid();

    [Fact]
    public async Task GetToolsForPersonaAsync_OrchestratorRole_ReturnsRegisteredTools()
    {
        var tool = CreateTool();
        ConfigurePersona(ToolRegistry.OrchestratorRole);
        var sut = CreateSut(tool);

        var result = await sut.GetToolsForPersonaAsync(
            _personaId,
            TestContext.Current.CancellationToken);

        result.Should().ContainSingle().Which.Name.Should().Be(tool.Definition.Name);
    }

    [Fact]
    public async Task GetToolsForPersonaAsync_RegularPersona_ReturnsEmpty()
    {
        ConfigurePersona("companion");
        var sut = CreateSut(CreateTool());

        var result = await sut.GetToolsForPersonaAsync(
            _personaId,
            TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsFailedResult()
    {
        ConfigurePersona(ToolRegistry.OrchestratorRole);
        var tool = CreateTool();
        var sut = CreateSut(tool);

        var result = await sut.ExecuteAsync(
            new ToolCall("call-1", "hallucinated_tool", "{}"),
            CreateContext(),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ToolExecutionStatus.Failed);
        result.Preview.Should().Contain("Unknown or unavailable");
        await tool.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<ToolContext>(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_EnabledTool_ReturnsItsResult()
    {
        ConfigurePersona(ToolRegistry.OrchestratorRole);
        var expected = new ToolResult("{\"ok\":true}", "ok", ToolExecutionStatus.Succeeded);
        var tool = CreateTool(expected);
        var sut = CreateSut(tool);

        var result = await sut.ExecuteAsync(
            new ToolCall("call-1", tool.Definition.Name, "{}"),
            CreateContext(),
            TestContext.Current.CancellationToken);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task ExecuteAsync_PassesToolContextIntact()
    {
        ConfigurePersona(ToolRegistry.OrchestratorRole);
        var tool = CreateTool();
        var context = CreateContext();
        var sut = CreateSut(tool);

        await sut.ExecuteAsync(
            new ToolCall("call-1", tool.Definition.Name, "{}"),
            context,
            TestContext.Current.CancellationToken);

        await tool.Received(1).ExecuteAsync(
            "{}",
            context,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidArgumentsJson_ReturnsFailedWithoutExecuting()
    {
        var tool = CreateTool();
        var sut = CreateSut(tool);

        var result = await sut.ExecuteAsync(
            new ToolCall("call-1", tool.Definition.Name, "{broken"),
            CreateContext(),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ToolExecutionStatus.Failed);
        result.Preview.Should().Contain("not valid JSON");
        await tool.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<ToolContext>(),
            TestContext.Current.CancellationToken);
        await _personaService.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_ToolThrows_ReturnsFailedResultWithErrorText()
    {
        ConfigurePersona(ToolRegistry.OrchestratorRole);
        var tool = CreateTool();
        tool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<ToolResult>>(_ => throw new InvalidOperationException("clock exploded"));
        var sut = CreateSut(tool);

        var result = await sut.ExecuteAsync(
            new ToolCall("call-1", tool.Definition.Name, "{}"),
            CreateContext(),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ToolExecutionStatus.Failed);
        result.Preview.Should().Be("clock exploded");
        result.PayloadJson.Should().Contain("clock exploded");
    }

    [Fact]
    public async Task GetCurrentTimeTool_ReturnsDeterministicUtcPayload()
    {
        var now = DateTimeOffset.Parse("2026-07-14T09:00:00+00:00");
        var sut = new GetCurrentTimeTool(new FixedTimeProvider(now));

        var result = await sut.ExecuteAsync(
            "{}",
            CreateContext(),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ToolExecutionStatus.Succeeded);
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("utc").GetString().Should().Be(now.ToString("O"));
    }

    private ToolRegistry CreateSut(params ITool[] tools)
    {
        return new ToolRegistry(
            _personaService,
            tools,
            NullLogger<ToolRegistry>.Instance);
    }

    private void ConfigurePersona(string role)
    {
        _personaService.GetByIdAsync(_personaId, Arg.Any<CancellationToken>())
            .Returns(new PersonaDto(
                _personaId,
                "Test persona",
                SystemPromptDto.Empty,
                null,
                role,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
    }

    private static ITool CreateTool(ToolResult? result = null)
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var tool = Substitute.For<ITool>();
        tool.Definition.Returns(new ToolDefinition(
            "get_current_time",
            "Get the current time.",
            schema.RootElement.Clone()));
        tool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(result ?? new ToolResult("{}", null, ToolExecutionStatus.Succeeded));
        return tool;
    }

    private ToolContext CreateContext()
    {
        return new ToolContext(Guid.NewGuid(), _personaId, Guid.NewGuid());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
