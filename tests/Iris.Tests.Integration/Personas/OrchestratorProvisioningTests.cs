using FluentAssertions;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Iris.Tests.Integration.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Personas;

[Collection("ApiTestFactory collection")]
public class OrchestratorProvisioningTests
{
    private readonly ApiTestFactory _factory;

    public OrchestratorProvisioningTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EnsureProvisioned_NewUser_CreatesOneSystemPersonaAndInitialConversation()
    {
        var userId = Guid.NewGuid();

        var result = await EnsureProvisionedAsync(userId);

        using var scope = CreateUserScope(userId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var personas = await db.Personas.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var conversations = await db.ConversationReadModels.AsNoTracking()
            .Where(conversation => conversation.PersonaId == result.PersonaId)
            .ToListAsync(TestContext.Current.CancellationToken);

        personas.Should().ContainSingle();
        personas[0].Kind.Should().Be(PersonaKind.System);
        (await db.SystemPrompts.CountAsync(
            prompt => prompt.PersonaId == result.PersonaId,
            TestContext.Current.CancellationToken)).Should().Be(0);
        conversations.Should().ContainSingle();
        conversations[0].Id.Should().Be(result.ConversationId);
        conversations[0].UserId.Should().Be(userId);
    }

    [Fact]
    public async Task EnsureProvisioned_RepeatedCall_ReturnsSamePersonaAndConversation()
    {
        var userId = Guid.NewGuid();

        var first = await EnsureProvisionedAsync(userId);
        var second = await EnsureProvisionedAsync(userId);

        second.Should().Be(first);

        using var scope = CreateUserScope(userId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Personas.CountAsync(
            persona => persona.Kind == PersonaKind.System,
            TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.ConversationReadModels.CountAsync(
            conversation => conversation.PersonaId == first.PersonaId,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task EnsureProvisioned_ConcurrentFirstLogins_AllReturnTheSingleProvisionedPair()
    {
        var userId = Guid.NewGuid();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => EnsureProvisionedAsync(userId)));

        results.Select(result => result.PersonaId).Distinct().Should().ContainSingle();
        results.Select(result => result.ConversationId).Distinct().Should().ContainSingle();

        using var scope = CreateUserScope(userId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Personas.CountAsync(
            persona => persona.Kind == PersonaKind.System,
            TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.ConversationReadModels.CountAsync(
            conversation => conversation.PersonaId == results[0].PersonaId,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task EnsureProvisioned_SystemPersonaWithoutConversation_RepairsMissingConversation()
    {
        var userId = Guid.NewGuid();
        var personaId = await SeedSystemPersonaAsync(userId);

        var result = await EnsureProvisionedAsync(userId);

        result.PersonaId.Should().Be(personaId);
        result.ConversationId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task EnsureProvisioned_ExistingOrchestratorConversation_DoesNotPreventLaterConversations()
    {
        var userId = Guid.NewGuid();
        var provisioned = await EnsureProvisionedAsync(userId);
        var additionalConversationId = Guid.NewGuid();

        await _factory.Services.SendCommandAsAsync(
            userId,
            new CreateConversationCommand(
                additionalConversationId,
                userId,
                provisioned.PersonaId,
                "Create another persona"),
            TestContext.Current.CancellationToken);

        using var scope = CreateUserScope(userId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = await db.ConversationReadModels
            .Where(conversation => conversation.PersonaId == provisioned.PersonaId)
            .Select(conversation => conversation.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        ids.Should().BeEquivalentTo([provisioned.ConversationId, additionalConversationId]);
    }

    private async Task<OrchestratorProvisioningResult> EnsureProvisionedAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var provisioner = scope.ServiceProvider.GetRequiredService<IOrchestratorProvisioner>();
        return await provisioner.EnsureProvisionedAsync(userId, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedSystemPersonaAsync(Guid userId)
    {
        using var scope = CreateUserScope(userId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var persona = new Persona
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Iris",
            Kind = PersonaKind.System,
            Role = "orchestrator",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Personas.Add(persona);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return persona.Id;
    }

    private IServiceScope CreateUserScope(Guid userId)
    {
        var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUserService>().OverrideUserId = userId;
        return scope;
    }
}
