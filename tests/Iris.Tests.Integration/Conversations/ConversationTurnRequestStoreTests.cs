using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Domain.Conversations.Entities;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iris.Tests.Integration.Conversations;

/// <summary>
/// Direct tests of the durable turn-request store's claim, retry, orphan-recovery
/// and cancellation semantics against a real Postgres, using raw DbContexts so the
/// behaviour is deterministic (no dependence on the background worker's timing).
/// </summary>
[Collection("IntegrationTestFactory collection")]
public class ConversationTurnRequestStoreTests
{
    private const int MaxAttempts = 2;

    private readonly IntegrationTestFactory _factory;

    public ConversationTurnRequestStoreTests(IntegrationTestFactory factory)
    {
        _factory = factory;
    }

    private async Task<ConversationTurnRequest> SeedPendingAsync(Guid conversationId, DateTimeOffset createdAt)
    {
        var request = new ConversationTurnRequest
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = Guid.NewGuid(),
            Model = "test/model",
            Status = ConversationTurnStatus.Pending,
            CreatedAt = createdAt,
        };

        await using var db = _factory.CreateDbContext();
        db.ConversationTurnRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request;
    }

    /// <summary>
    /// Seeds a row directly in Processing (as if freshly claimed). Used by the
    /// terminal-guard tests so they don't depend on ClaimPendingAsync, whose
    /// table-wide claim could pick up leftover Pending rows from other tests.
    /// </summary>
    private async Task<ConversationTurnRequest> SeedProcessingAsync(Guid conversationId)
    {
        var request = new ConversationTurnRequest
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = Guid.NewGuid(),
            Model = "test/model",
            Status = ConversationTurnStatus.Processing,
            AttemptCount = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        await using var db = _factory.CreateDbContext();
        db.ConversationTurnRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request;
    }

    private EfConversationTurnRequestStore CreateStore(AppDbContext db) => new(db);

    private async Task<ConversationTurnRequest?> ReloadAsync(Guid id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ConversationTurnRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == id, TestContext.Current.CancellationToken);
    }

    // ── Atomic enqueue guard (4.2) ────────────────────────────────

    [Fact]
    public async Task AddPending_WithoutSaveChanges_DoesNotPersist()
    {
        // AddPending must only TRACK the row; the commit is the caller's single
        // SaveChangesAsync (via the event recorder). If SaveChanges never runs —
        // e.g. the event append throws first — nothing must be persisted.
        var conversationId = Guid.NewGuid();
        var id = Guid.NewGuid();

        await using (var db = _factory.CreateDbContext())
        {
            var store = CreateStore(db);
            store.AddPending(new ConversationTurnRequest
            {
                Id = id,
                ConversationId = conversationId,
                UserId = Guid.NewGuid(),
                Model = "test/model",
                Status = ConversationTurnStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            // Deliberately dispose WITHOUT calling SaveChangesAsync — simulates an
            // append failure between AddPending and the shared commit.
        }

        var persisted = await ReloadAsync(id);
        persisted.Should().BeNull("AddPending only tracks; without SaveChanges nothing is committed");
    }

    // ── Claim semantics (4.5) ─────────────────────────────────────

    [Fact]
    public async Task Claim_TwoConversations_ClaimsBothInParallel()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var a = await SeedPendingAsync(conversationA, now);
        var b = await SeedPendingAsync(conversationB, now);

        await using var db = _factory.CreateDbContext();
        var claimed = await CreateStore(db).ClaimPendingAsync(8, MaxAttempts, TestContext.Current.CancellationToken);

        // The store claims table-wide, so scope the state assertions to the two rows
        // this test seeded (other tests' leftover Pending rows may also be claimed).
        var mine = claimed.Where(r => r.Id == a.Id || r.Id == b.Id).ToList();
        mine.Select(r => r.Id).Should().BeEquivalentTo(new[] { a.Id, b.Id });
        mine.Should().OnlyContain(r => r.Status == ConversationTurnStatus.Processing);
        mine.Should().OnlyContain(r => r.AttemptCount == 1);
    }

    [Fact]
    public async Task Claim_SameConversation_SecondNotClaimableWhileFirstProcessing()
    {
        var conversationId = Guid.NewGuid();
        var first = await SeedPendingAsync(conversationId, DateTimeOffset.UtcNow.AddSeconds(-10));
        var second = await SeedPendingAsync(conversationId, DateTimeOffset.UtcNow);

        // First claim round takes only the oldest row.
        await using (var db1 = _factory.CreateDbContext())
        {
            var claimed1 = await CreateStore(db1).ClaimPendingAsync(8, MaxAttempts, TestContext.Current.CancellationToken);
            claimed1.Should().ContainSingle(r => r.Id == first.Id);
            claimed1.Should().NotContain(r => r.Id == second.Id);
        }

        // Second claim round: the first row is still Processing, so the conversation
        // is excluded entirely — the second row is NOT claimable.
        await using (var db2 = _factory.CreateDbContext())
        {
            var claimed2 = await CreateStore(db2).ClaimPendingAsync(8, MaxAttempts, TestContext.Current.CancellationToken);
            claimed2.Should().NotContain(r => r.Id == second.Id);
        }

        // Complete the first; now the second becomes claimable.
        await using (var completeDb = _factory.CreateDbContext())
        {
            await CreateStore(completeDb).MarkCompletedAsync(first.Id, TestContext.Current.CancellationToken);
        }

        await using (var db3 = _factory.CreateDbContext())
        {
            var claimed3 = await CreateStore(db3).ClaimPendingAsync(8, MaxAttempts, TestContext.Current.CancellationToken);
            claimed3.Should().ContainSingle(r => r.Id == second.Id);
        }
    }

    [Fact]
    public async Task Claim_RespectsMaxCount()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedPendingAsync(Guid.NewGuid(), now);
        await SeedPendingAsync(Guid.NewGuid(), now);
        await SeedPendingAsync(Guid.NewGuid(), now);

        await using var db = _factory.CreateDbContext();
        var claimed = await CreateStore(db).ClaimPendingAsync(2, MaxAttempts, TestContext.Current.CancellationToken);

        claimed.Should().HaveCount(2);
    }

    // ── Orphan recovery (4.3) ─────────────────────────────────────

    [Fact]
    public async Task RecoverOrphans_StaleProcessingUnderCap_ResetToPending()
    {
        var conversationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = id,
                ConversationId = conversationId,
                UserId = Guid.NewGuid(),
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var recoverDb = _factory.CreateDbContext())
        {
            var failed = await CreateStore(recoverDb)
                .RecoverOrphansAsync(TimeSpan.FromMinutes(5), MaxAttempts, [], TestContext.Current.CancellationToken);
            failed.Should().BeEmpty("row is under the attempt cap so it is reset, not failed");
        }

        var reloaded = await ReloadAsync(id);
        reloaded!.Status.Should().Be(ConversationTurnStatus.Pending);
        reloaded.ClaimedAt.Should().BeNull();
    }

    [Fact]
    public async Task RecoverOrphans_StaleProcessingAtCap_ReturnedButNotMutated()
    {
        // At-cap orphans are RETURNED without mutation so the caller records the
        // terminal TurnFailed event FIRST, then flips the row Failed (event-before-
        // row ordering). The store must leave the row Processing here.
        var conversationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = id,
                ConversationId = conversationId,
                UserId = Guid.NewGuid(),
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = MaxAttempts,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        IReadOnlyList<ConversationTurnRequest> atCap;
        await using (var recoverDb = _factory.CreateDbContext())
        {
            atCap = await CreateStore(recoverDb)
                .RecoverOrphansAsync(TimeSpan.FromMinutes(5), MaxAttempts, [], TestContext.Current.CancellationToken);
        }

        atCap.Should().ContainSingle(r => r.Id == id);

        var reloaded = await ReloadAsync(id);
        reloaded!.Status.Should().Be(ConversationTurnStatus.Processing,
            "the store returns at-cap candidates without mutating them; the caller flips the row Failed after recording the event");

        // The caller then marks it Failed (mirrors the worker's event-then-flip path).
        await using (var failDb = _factory.CreateDbContext())
        {
            await CreateStore(failDb).MarkFailedAsync(id, "interrupted", TestContext.Current.CancellationToken);
        }

        (await ReloadAsync(id))!.Status.Should().Be(ConversationTurnStatus.Failed);
    }

    [Fact]
    public async Task RecoverOrphans_LocallyActiveConversation_NotResetEvenIfStale()
    {
        // A live long-running stream in THIS process: its ClaimedAt is stale but the
        // conversation is in the active set, so orphan recovery must skip it entirely.
        var conversationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = id,
                ConversationId = conversationId,
                UserId = Guid.NewGuid(),
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20), // stale
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var recoverDb = _factory.CreateDbContext())
        {
            await CreateStore(recoverDb)
                .RecoverOrphansAsync(TimeSpan.FromMinutes(5), MaxAttempts, [conversationId], TestContext.Current.CancellationToken);
        }

        var reloaded = await ReloadAsync(id);
        reloaded!.Status.Should().Be(ConversationTurnStatus.Processing,
            "the conversation is streaming locally, so lease expiry must not reset it");
    }

    [Fact]
    public async Task RecoverOrphans_FreshProcessing_NotTouched()
    {
        var id = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = id,
                ConversationId = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ClaimedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var recoverDb = _factory.CreateDbContext())
        {
            await CreateStore(recoverDb)
                .RecoverOrphansAsync(TimeSpan.FromMinutes(5), MaxAttempts, [], TestContext.Current.CancellationToken);
        }

        var reloaded = await ReloadAsync(id);
        reloaded!.Status.Should().Be(ConversationTurnStatus.Processing, "the claim is still within its lease");
    }

    // ── Cancellation lookup ───────────────────────────────────────

    [Fact]
    public async Task GetActive_ReturnsPendingRow()
    {
        var conversationId = Guid.NewGuid();
        var pending = await SeedPendingAsync(conversationId, DateTimeOffset.UtcNow);

        await using var db = _factory.CreateDbContext();
        var active = await CreateStore(db).GetActiveAsync(conversationId, TestContext.Current.CancellationToken);

        active.Should().ContainSingle();
        active[0].Id.Should().Be(pending.Id);
        active[0].Status.Should().Be(ConversationTurnStatus.Pending);
    }

    [Fact]
    public async Task GetActive_ReturnsAllActiveRowsNewestFirst()
    {
        // "Stop generating" must be able to cancel EVERY active turn, so the store
        // returns all Pending/Processing rows (newest first), not just the latest.
        var conversationId = Guid.NewGuid();
        var older = await SeedPendingAsync(conversationId, DateTimeOffset.UtcNow.AddSeconds(-10));
        var newer = await SeedPendingAsync(conversationId, DateTimeOffset.UtcNow);

        await using var db = _factory.CreateDbContext();
        var active = await CreateStore(db).GetActiveAsync(conversationId, TestContext.Current.CancellationToken);

        active.Select(r => r.Id).Should().Equal(newer.Id, older.Id);
    }

    [Fact]
    public async Task GetActive_NoActiveTurn_ReturnsEmpty()
    {
        var conversationId = Guid.NewGuid();
        var processing = await SeedProcessingAsync(conversationId);

        await using (var completeDb = _factory.CreateDbContext())
        {
            await CreateStore(completeDb).MarkCompletedAsync(processing.Id, TestContext.Current.CancellationToken);
        }

        await using var db = _factory.CreateDbContext();
        var active = await CreateStore(db).GetActiveAsync(conversationId, TestContext.Current.CancellationToken);

        active.Should().BeEmpty();
    }

    // ── Terminal-state guards ─────────────────────────────────────

    [Fact]
    public async Task MarkCancelled_AfterCompleted_RowStaysCompleted()
    {
        // Race: a user cancel lands just as the stream completes. Terminal states
        // must not overwrite each other — the first terminal writer wins.
        var conversationId = Guid.NewGuid();
        var request = await SeedProcessingAsync(conversationId);

        await using (var completeDb = _factory.CreateDbContext())
        {
            await CreateStore(completeDb).MarkCompletedAsync(request.Id, TestContext.Current.CancellationToken);
        }

        await using (var cancelDb = _factory.CreateDbContext())
        {
            await CreateStore(cancelDb).MarkCancelledAsync(request.Id, TestContext.Current.CancellationToken);
        }

        var reloaded = await ReloadAsync(request.Id);
        reloaded!.Status.Should().Be(ConversationTurnStatus.Completed,
            "a terminal Completed row must not be overwritten by a late cancel");
    }

    [Fact]
    public async Task MarkCompleted_AfterCancelled_RowStaysCancelled()
    {
        // The mirror race: the cancel endpoint wins, then the worker's completion
        // path fires. The zero-row update must be silently ignored.
        var conversationId = Guid.NewGuid();
        var request = await SeedProcessingAsync(conversationId);

        await using (var cancelDb = _factory.CreateDbContext())
        {
            await CreateStore(cancelDb).MarkCancelledAsync(request.Id, TestContext.Current.CancellationToken);
        }

        await using (var completeDb = _factory.CreateDbContext())
        {
            await CreateStore(completeDb).MarkCompletedAsync(request.Id, TestContext.Current.CancellationToken);
        }

        var reloaded = await ReloadAsync(request.Id);
        reloaded!.Status.Should().Be(ConversationTurnStatus.Cancelled,
            "a terminal Cancelled row must not be overwritten by a late completion");
    }

    [Fact]
    public async Task MarkCancelled_PendingRow_StillWorks()
    {
        // The cancel-before-claim flavour: MarkCancelledAsync's guard must allow
        // Pending as well as Processing.
        var conversationId = Guid.NewGuid();
        var request = await SeedPendingAsync(conversationId, DateTimeOffset.UtcNow);

        await using (var cancelDb = _factory.CreateDbContext())
        {
            await CreateStore(cancelDb).MarkCancelledAsync(request.Id, TestContext.Current.CancellationToken);
        }

        var reloaded = await ReloadAsync(request.Id);
        reloaded!.Status.Should().Be(ConversationTurnStatus.Cancelled);
    }
}
