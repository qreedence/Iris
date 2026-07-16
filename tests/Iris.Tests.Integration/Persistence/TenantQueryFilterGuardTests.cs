using FluentAssertions;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.AiIntegration;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Iris.Tests.Integration.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Iris.Domain.Conversations.Content;

namespace Iris.Tests.Integration.Persistence;

/// <summary>
/// Guard tests for the EF global query filters that are the canonical
/// tenant-isolation mechanism for Persona, SystemPrompt, and
/// ConversationReadModel (see AppDbContext.OnModelCreating). Each test seeds a
/// row as user A, then reads it back in a fresh scope as user B and proves two
/// things: (1) the normal DbSet query returns nothing — the filter is doing its
/// job — and (2) an IgnoreQueryFilters() query still finds the row, proving the
/// data genuinely exists and it's the filter (not seeding failure or a WHERE
/// clause elsewhere) that hides it. If someone ever deletes one of the
/// HasQueryFilter calls in AppDbContext, the first assertion in the
/// corresponding test starts failing loudly.
/// </summary>
[Collection("ApiTestFactory collection")]
public class TenantQueryFilterGuardTests
{
    private readonly ApiTestFactory _factory;
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();

    public TenantQueryFilterGuardTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private IServiceScope CreateScopeAs(Guid userId)
    {
        var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = userId;
        return scope;
    }

    private async Task<Guid> CreatePersonaAsync(Guid userId)
    {
        var persona = await TestPersonas.CreateAsync(
            _factory.Services, userId, ct: TestContext.Current.CancellationToken);
        return persona.Id;
    }

    private Task<Guid> CreateConversationAsync(Guid userId, Guid personaId) =>
        TestConversations.CreateAsync(
            _factory.Services, userId, personaId, "Guarded Chat", TestContext.Current.CancellationToken);

    // ── Persona ──────────────────────────────────────────────────

    [Fact]
    public async Task Persona_QueryFilter_HidesOtherUsersRowUnlessIgnored()
    {
        // Arrange — seed a persona as user A.
        var personaId = await CreatePersonaAsync(_userA);

        // Act — query as user B, both filtered and unfiltered.
        using var scope = CreateScopeAs(_userB);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var filtered = await db.Personas
            .Where(p => p.Id == personaId)
            .ToListAsync(TestContext.Current.CancellationToken);

        var unfiltered = await db.Personas
            .IgnoreQueryFilters()
            .Where(p => p.Id == personaId)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        filtered.Should().BeEmpty("the Persona query filter should hide user A's row from user B");
        unfiltered.Should().ContainSingle(p => p.Id == personaId,
            "the row genuinely exists — IgnoreQueryFilters proves the filter, not missing data, is what hid it");
    }

    // ── SystemPrompt ─────────────────────────────────────────────

    [Fact]
    public async Task SystemPrompt_QueryFilter_HidesOtherUsersRowUnlessIgnored()
    {
        // Arrange — every persona gets a SystemPrompt row created alongside it.
        var personaId = await CreatePersonaAsync(_userA);

        // Act — query as user B, both filtered and unfiltered.
        using var scope = CreateScopeAs(_userB);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var filtered = await db.SystemPrompts
            .Where(sp => sp.PersonaId == personaId)
            .ToListAsync(TestContext.Current.CancellationToken);

        var unfiltered = await db.SystemPrompts
            .IgnoreQueryFilters()
            .Where(sp => sp.PersonaId == personaId)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        filtered.Should().BeEmpty("the SystemPrompt query filter (via Persona.UserId) should hide user A's row from user B");
        unfiltered.Should().ContainSingle(sp => sp.PersonaId == personaId,
            "the row genuinely exists — IgnoreQueryFilters proves the filter, not missing data, is what hid it");
    }

    // ── ConversationReadModel ────────────────────────────────────

    [Fact]
    public async Task ConversationReadModel_QueryFilter_HidesOtherUsersRowUnlessIgnored()
    {
        // Arrange — seed a conversation as user A (persona must also belong to A).
        var personaId = await CreatePersonaAsync(_userA);
        var conversationId = await CreateConversationAsync(_userA, personaId);

        // Act — query as user B, both filtered and unfiltered.
        using var scope = CreateScopeAs(_userB);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var filtered = await db.ConversationReadModels
            .Where(c => c.Id == conversationId)
            .ToListAsync(TestContext.Current.CancellationToken);

        var unfiltered = await db.ConversationReadModels
            .IgnoreQueryFilters()
            .Where(c => c.Id == conversationId)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        filtered.Should().BeEmpty("the ConversationReadModel query filter should hide user A's conversation from user B");
        unfiltered.Should().ContainSingle(c => c.Id == conversationId,
            "the row genuinely exists — IgnoreQueryFilters proves the filter, not missing data, is what hid it");
    }

    // ── ConversationMessages — intentionally NOT filtered ───────────

    [Fact]
    public async Task ConversationMessages_HasNoQueryFilter_OtherUsersMessagesAreVisibleWithoutIgnoreQueryFilters()
    {
        // This is the mirror image of the tests above, and it documents a
        // deliberate design choice: ConversationMessages has NO EF query filter
        // (see AppDbContext.OnModelCreating — only Persona, SystemPrompt, and
        // ConversationReadModel get HasQueryFilter). Tenant isolation for
        // messages is instead enforced by the explicit ExistsForUserAsync
        // pre-check in ConversationQueries.GetMessagesAsync, which is the only
        // thing stopping cross-tenant reads through that code path.
        //
        // This test asserts that a *raw* query against ConversationMessages
        // (bypassing GetMessagesAsync entirely) returns another user's rows with
        // no IgnoreQueryFilters needed — because there is no filter to ignore.
        // If this test ever starts failing because the query comes back empty,
        // it means a query filter was added to ConversationMessages; that's a
        // meaningful design change and GetMessagesAsync's pre-check comment
        // should be revisited (the pre-check would become redundant-but-safe,
        // not load-bearing). If it fails because GetMessagesAsync stopped
        // pre-checking, that's the isolation regression this whole suite exists
        // to catch elsewhere.
        var personaId = await CreatePersonaAsync(_userA);
        var conversationId = await CreateConversationAsync(_userA, personaId);

        using (var seedScope = CreateScopeAs(_userA))
        {
            await ConversationSeeder.SendMessageAsync(
                seedScope.ServiceProvider,
                conversationId,
                "User A's private message",
                ChatRole.User,
                overrideUserId: _userA,
                ct: TestContext.Current.CancellationToken);
        }

        // Act — query ConversationMessages directly as user B, no IgnoreQueryFilters.
        using var scope = CreateScopeAs(_userB);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var messages = await db.ConversationMessages
            .Where(m => m.ConversationId == conversationId)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        messages.Should().ContainSingle(m => MessageContentBlocks.ToVisibleText(m.ContentBlocks) == "User A's private message",
            "ConversationMessages is intentionally unfiltered; isolation for it comes from " +
            "ConversationQueries.GetMessagesAsync's ExistsForUserAsync pre-check, not a query filter");
    }

    // ── Uploads ───────────────────────────────────────────────────

    [Fact]
    public async Task Uploads_QueryFilter_HidesOtherUsersRowUnlessIgnored()
    {
        var uploadId = Guid.NewGuid();

        using (var seedScope = CreateScopeAs(_userA))
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Uploads.Add(new Upload
            {
                Id = uploadId,
                UserId = _userA,
                Status = UploadStatus.Confirmed,
                ContentType = "image/png",
                StorageKey = $"uploads/{uploadId}.png",
                SizeBytes = 1234,
                OriginalFileName = "thinking.png",
                CreatedAt = DateTimeOffset.UtcNow,
                ConfirmedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope = CreateScopeAs(_userB);
        var readDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var filtered = await readDb.Uploads
            .Where(u => u.Id == uploadId)
            .ToListAsync(TestContext.Current.CancellationToken);

        var unfiltered = await readDb.Uploads
            .IgnoreQueryFilters()
            .Where(u => u.Id == uploadId)
            .ToListAsync(TestContext.Current.CancellationToken);

        filtered.Should().BeEmpty("the Upload query filter should hide user A's uploaded payloads from user B");
        unfiltered.Should().ContainSingle(u => u.UserId == _userA,
            "the row genuinely exists — IgnoreQueryFilters proves the filter, not missing data, is what hid it");
    }

    // ── ToolResultPayloads — intentionally NOT filtered ───────────

    [Fact]
    public async Task ToolResultPayloads_HasNoQueryFilter_OtherUsersRowsAreVisibleWithoutIgnoreQueryFilters()
    {
        // Tool result payloads are conversation-scoped payload storage. Like
        // ConversationMessages, direct DbSet access is intentionally unfiltered;
        // tenant isolation belongs at the conversation command/query boundary.
        var personaId = await CreatePersonaAsync(_userA);
        var conversationId = await CreateConversationAsync(_userA, personaId);
        var payloadId = Guid.NewGuid();

        using (var seedScope = CreateScopeAs(_userA))
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ToolResultPayloads.Add(new ToolResultPayload
            {
                Id = payloadId,
                ConversationId = conversationId,
                ToolCallId = "tool-call-1",
                PayloadJson = "{\"ok\":true}",
                Preview = "ok",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope = CreateScopeAs(_userB);
        var readDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await readDb.ToolResultPayloads
            .Where(p => p.Id == payloadId)
            .ToListAsync(TestContext.Current.CancellationToken);

        rows.Should().ContainSingle(p => p.ConversationId == conversationId,
            "ToolResultPayloads is intentionally unfiltered; isolation comes from the owning conversation access path");
    }

    // ── PersonaCreationToolExecutions — intentionally NOT filtered ──

    [Fact]
    public async Task PersonaCreationToolExecutions_HasNoQueryFilter_OtherUsersRowsAreVisibleWithoutIgnoreQueryFilters()
    {
        // This is a conversation-scoped idempotency ledger, not a user-facing
        // query surface. Like ToolResultPayloads, isolation is enforced through
        // the owning conversation before tool execution reaches this table.
        var personaId = await CreatePersonaAsync(_userA);
        var conversationId = await CreateConversationAsync(_userA, personaId);

        using (var seedScope = CreateScopeAs(_userA))
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PersonaCreationToolExecutions.Add(new PersonaCreationToolExecution
            {
                ConversationId = conversationId,
                ToolCallId = "create-persona-call-1",
                PersonaId = personaId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope = CreateScopeAs(_userB);
        var readDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await readDb.PersonaCreationToolExecutions
            .Where(execution => execution.ConversationId == conversationId)
            .ToListAsync(TestContext.Current.CancellationToken);

        rows.Should().ContainSingle(execution => execution.PersonaId == personaId,
            "PersonaCreationToolExecutions is intentionally unfiltered; isolation comes from the owning conversation access path");
    }

    // ── ConversationTurnRequests — intentionally NOT filtered ───────

    [Fact]
    public async Task ConversationTurnRequests_HasNoQueryFilter_OtherUsersRowsAreVisibleWithoutIgnoreQueryFilters()
    {
        // Like ConversationMessages and StoredEvents, conversation_turn_requests is
        // a system/worker table with NO EF query filter (see
        // AppDbContext.OnModelCreating). The background worker must be able to claim
        // and process turn requests across ALL users, so a per-user filter would
        // break it. Tenant isolation for enqueue/cancel is enforced instead by the
        // ExistsForUserAsync ownership check at the command boundary
        // (StartConversationTurnHandler / CancelConversationTurnHandler).
        //
        // This test seeds a turn request as user A, then reads it back directly as
        // user B with NO IgnoreQueryFilters — it must be visible, because there is
        // no filter to ignore. If it ever comes back empty, a query filter was added
        // to ConversationTurnRequests, which would silently break the worker.
        var conversationId = Guid.NewGuid();

        using (var seedScope = CreateScopeAs(_userA))
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ConversationTurnRequests.Add(new Iris.Domain.Conversations.Entities.ConversationTurnRequest
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                UserId = _userA,
                Model = "test/model",
                Status = Iris.Domain.Conversations.Entities.ConversationTurnStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act — query directly as user B, no IgnoreQueryFilters.
        using var scope = CreateScopeAs(_userB);
        var readDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await readDb.ConversationTurnRequests
            .Where(r => r.ConversationId == conversationId)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        rows.Should().ContainSingle(r => r.UserId == _userA,
            "ConversationTurnRequests is intentionally unfiltered so the background worker " +
            "can process turns across all users; isolation comes from the command-boundary " +
            "ExistsForUserAsync check, not a query filter");
    }
}
