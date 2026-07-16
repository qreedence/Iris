using System.Text.Json;
using FluentAssertions;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.AiIntegration;

[Collection("ApiTestFactory collection")]
public class CreatePersonaToolTests
{
    private readonly ApiTestFactory _factory;

    public CreatePersonaToolTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExecuteAsync_ValidArguments_CreatesUserPersonaWithSeededRoleAndEmptyPrompt()
    {
        var userId = Guid.NewGuid();
        using var scope = await CreateToolScopeAsync(userId);
        var tool = GetCreatePersonaTool(scope);
        var context = CreateContext(userId);

        var result = await tool.ExecuteAsync(
            """{"name":"Atlas","role":"Study buddy"}""",
            context,
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ToolExecutionStatus.Succeeded);
        using var payload = JsonDocument.Parse(result.PayloadJson);
        var personaId = payload.RootElement.GetProperty("id").GetGuid();
        payload.RootElement.GetProperty("name").GetString().Should().Be("Atlas");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persona = await db.Personas.Include(item => item.SystemPrompt)
            .SingleAsync(item => item.Id == personaId, TestContext.Current.CancellationToken);
        persona.UserId.Should().Be(userId);
        persona.Kind.Should().Be(PersonaKind.User);
        persona.Role.Should().Be("Study buddy");
        persona.SystemPrompt.Should().NotBeNull();
        persona.SystemPrompt!.Identity.Should().BeNull();
        persona.SystemPrompt.Voice.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SameToolCallTwice_ReturnsSamePersonaWithoutDuplicateSideEffect()
    {
        var userId = Guid.NewGuid();
        using var scope = await CreateToolScopeAsync(userId);
        var tool = GetCreatePersonaTool(scope);
        var context = CreateContext(userId);

        var first = await tool.ExecuteAsync(
            """{"name":"Atlas","role":"Study buddy"}""",
            context,
            TestContext.Current.CancellationToken);
        var second = await tool.ExecuteAsync(
            """{"name":"Changed on retry","role":"Different"}""",
            context,
            TestContext.Current.CancellationToken);

        first.PayloadJson.Should().Be(second.PayloadJson);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Personas.CountAsync(
            persona => persona.Kind == PersonaKind.User,
            TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.PersonaCreationToolExecutions.CountAsync(
            execution => execution.ConversationId == context.ConversationId
                && execution.ToolCallId == context.ToolCallId,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Theory]
    [InlineData("{}", "Persona name is required.")]
    [InlineData("{\"name\":\"Atlas\"}", "Persona role is required.")]
    [InlineData("{broken", "Tool arguments were not valid")]
    public async Task ExecuteAsync_InvalidArguments_ReturnsActionableFailureWithoutCreatingPersona(
        string arguments,
        string expectedMessage)
    {
        var userId = Guid.NewGuid();
        using var scope = await CreateToolScopeAsync(userId);
        var tool = GetCreatePersonaTool(scope);

        var result = await tool.ExecuteAsync(
            arguments,
            CreateContext(userId),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ToolExecutionStatus.Failed);
        result.Preview.Should().Contain(expectedMessage);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Personas.CountAsync(
            persona => persona.Kind == PersonaKind.User,
            TestContext.Current.CancellationToken)).Should().Be(0);
    }

    private async Task<IServiceScope> CreateToolScopeAsync(Guid userId)
    {
        using (var provisioningScope = _factory.Services.CreateScope())
        {
            var provisioner = provisioningScope.ServiceProvider.GetRequiredService<IOrchestratorProvisioner>();
            await provisioner.EnsureProvisionedAsync(userId, TestContext.Current.CancellationToken);
        }

        var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUserService>().OverrideUserId = userId;
        return scope;
    }

    private static ITool GetCreatePersonaTool(IServiceScope scope)
    {
        return scope.ServiceProvider.GetServices<ITool>()
            .Single(tool => tool.Definition.Name == "create_persona");
    }

    private static ToolContext CreateContext(Guid userId) => new(
        userId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        $"call-{Guid.NewGuid()}");
}
