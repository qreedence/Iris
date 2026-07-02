using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using Iris.Tests.Integration.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Iris.Tests.Integration.Conversations;

/// <summary>
/// End-to-end coverage of the durable turn worker through the real HTTP pipeline:
/// concurrency overlap, attempt-cap failure, and per-turn cancellation (both the
/// Pending and Processing flavours).
/// </summary>
[Collection("ApiTestFactory collection")]
public class ConversationTurnWorkerTests
{
    private readonly ApiTestFactory _factory;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly HttpClient _client;

    public ConversationTurnWorkerTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient(_userId);
    }

    private Task SendCommandAs<TResponse>(Guid userId, IRequest<TResponse> command) =>
        _factory.Services.SendCommandAsAsync(userId, command, TestContext.Current.CancellationToken);

    private async Task<Guid> CreatePersonaAsync()
    {
        var persona = await TestPersonas.CreateAsync(
            _factory.Services, _userId, ct: TestContext.Current.CancellationToken);
        return persona.Id;
    }

    private async Task<Guid> CreateConversationAsync()
    {
        var personaId = await CreatePersonaAsync();
        return await TestConversations.CreateAsync(
            _factory.Services, _userId, personaId, "Chat", TestContext.Current.CancellationToken);
    }

    private Task PostChatAsync(Guid conversationId, string userMessage) =>
        _client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/chat",
            new ChatRequestDto(userMessage, "test/model"),
            TestContext.Current.CancellationToken);

    private async Task<IReadOnlyList<ConversationEvent>> LoadStreamAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = _userId;
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        return await store.LoadStreamAsync(conversationId, TestContext.Current.CancellationToken);
    }

    private async Task<ConversationTurnStatus?> GetTurnStatusAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ConversationTurnRequests
            .AsNoTracking()
            .Where(r => r.ConversationId == conversationId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        return row?.Status;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            await Task.Delay(25, cts.Token);
        }
    }

    // ── Concurrency overlap proof (4.5) ───────────────────────────

    [Fact]
    public async Task TwoConversations_StreamsOverlapInTime()
    {
        // Two separate conversations whose streams block on a gate. If the worker
        // processed serially, the second stream would not START until the first
        // finished — the gate would deadlock. Overlap proves cross-conversation
        // parallelism.
        var started = new ConcurrentDictionary<string, TaskCompletionSource> ();
        var release = new TaskCompletionSource();

        var markerA = $"overlap-A-{Guid.NewGuid()}";
        var markerB = $"overlap-B-{Guid.NewGuid()}";
        started[markerA] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        started[markerB] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                var last = request.Messages[^1].Content;
                var ct = call.ArgAt<CancellationToken>(1);
                if (last == markerA || last == markerB)
                    return GatedStream(started[last], release.Task, ct);
                return DefaultStream(ct);
            });

        var conversationA = await CreateConversationAsync();
        var conversationB = await CreateConversationAsync();

        await PostChatAsync(conversationA, markerA);
        await PostChatAsync(conversationB, markerB);

        // Both streams must have STARTED concurrently (neither has been released).
        await Task.WhenAll(started[markerA].Task, started[markerB].Task)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Release both and let them complete.
        release.SetResult();

        await WaitUntilAsync(async () =>
            await GetTurnStatusAsync(conversationA) == ConversationTurnStatus.Completed &&
            await GetTurnStatusAsync(conversationB) == ConversationTurnStatus.Completed);
    }

    // ── Attempt cap → Failed + TurnFailed (4.5) ───────────────────

    [Fact]
    public async Task OrphanedTurnAtAttemptCap_MarkedFailedAndRecordsTurnFailed()
    {
        // A turn whose worker crashed mid-stream leaves a stale Processing row.
        // Seeded at the attempt cap so orphan recovery cannot retry it: the worker's
        // recovery tick marks it Failed AND records a TurnFailed("interrupted")
        // event. (Provider-level errors are terminal inside the orchestrator, which
        // records TurnFailed itself and returns normally — so the durable-queue
        // attempt cap governs orchestration interruptions, exercised here.)
        var conversationId = await CreateConversationAsync();
        var turnId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = turnId,
                ConversationId = conversationId,
                UserId = _userId,
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = 2, // == default MaxAttempts
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20), // older than the 5-min lease
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await WaitUntilAsync(
            async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Failed,
            TimeSpan.FromSeconds(20));

        var stream = await LoadStreamAsync(conversationId);
        stream.OfType<TurnFailed>().Should().ContainSingle(e => e.ErrorCode == "interrupted");
    }

    // ── Retry idempotency via MessageId linkage ───────────────────

    [Fact]
    public async Task CrashedTurnWithLaterTurnQueued_RetryUsesItsOwnMessage_NoDoubleStream()
    {
        // Scenario: turn 1 completed its stream but the worker crashed before
        // marking the row (stale Processing). While it sat crashed, turn 2 was
        // enqueued (Pending). On retry, turn 1's idempotency check must key off
        // ITS OWN MessageSent (via the MessageId linkage) — not the latest message
        // in the stream, which now belongs to turn 2. Correct behaviour: turn 1 is
        // marked Completed WITHOUT re-streaming (no token double-spend); turn 2
        // then streams normally. Exactly ONE provider call for this conversation.
        var marker1 = $"idem-turn1-{Guid.NewGuid()}";
        var marker2 = $"idem-turn2-{Guid.NewGuid()}";
        var providerCalls = 0;

        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                var ct = call.ArgAt<CancellationToken>(1);
                if (request.Messages.Any(m => m.Content == marker1))
                    Interlocked.Increment(ref providerCalls);
                return DefaultStream(ct);
            });

        var conversationId = await CreateConversationAsync();

        // Seed the event stream: turn 1's message + its full terminal outcome
        // (the stream finished), then turn 2's message (enqueued during the crash).
        var message1Id = Guid.NewGuid();
        var message2Id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
            userService.OverrideUserId = _userId;
            var recorder = scope.ServiceProvider.GetRequiredService<IConversationEventRecorder>();
            await recorder.RecordAsync(
                conversationId,
                [
                    new MessageSent(message1Id, conversationId, marker1, Iris.Domain.AiIntegration.ChatRole.User),
                    new AssistantResponseCompleted(Guid.NewGuid(), conversationId, "turn 1 response", "test/model"),
                    new TurnCompleted(conversationId, 1, 1),
                    new MessageSent(message2Id, conversationId, marker2, Iris.Domain.AiIntegration.ChatRole.User),
                ],
                TestContext.Current.CancellationToken);
        }

        // Seed the rows: turn 1 as a stale Processing orphan at attempt 1 (crashed
        // after its stream completed but before the row was marked), turn 2 Pending.
        var turn1Id = Guid.NewGuid();
        var turn2Id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = turn1Id,
                ConversationId = conversationId,
                UserId = _userId,
                MessageId = message1Id,
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20), // stale → orphan
            });
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = turn2Id,
                ConversationId = conversationId,
                UserId = _userId,
                MessageId = message2Id,
                Model = "test/model",
                Status = ConversationTurnStatus.Pending,
                AttemptCount = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Worker: orphan-recovers turn 1 → Pending, reclaims it (attempt 2) →
        // idempotency check finds turn 1's OWN terminal event → Completed, no
        // stream. Then turn 2 becomes claimable and streams.
        await WaitUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.ConversationTurnRequests
                .AsNoTracking()
                .Where(r => r.ConversationId == conversationId)
                .ToListAsync(TestContext.Current.CancellationToken);
            return rows.Count == 2 && rows.All(r => r.Status == ConversationTurnStatus.Completed);
        }, TimeSpan.FromSeconds(20));

        providerCalls.Should().Be(1,
            "turn 1's retry must skip streaming (its own terminal event exists); only turn 2 streams");

        // The stream gained exactly one more terminal pair (turn 2's) — turn 1 was
        // not re-streamed.
        var stream = await LoadStreamAsync(conversationId);
        stream.OfType<TurnCompleted>().Should().HaveCount(2);
    }

    // ── Cancellation: Pending flavour (4.4) ───────────────────────

    [Fact]
    public async Task CancelChat_PendingTurn_MarksCancelled()
    {
        var conversationId = await CreateConversationAsync();

        // Block the conversation with a FRESH Processing row (recent ClaimedAt, so
        // orphan recovery leaves it alone). The claim query then excludes this
        // conversation entirely, so the Pending row below can never be claimed —
        // making the "cancel a Pending, never-streamed turn" path deterministic.
        var pendingTurnId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                UserId = _userId,
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                ClaimedAt = DateTimeOffset.UtcNow, // fresh → not an orphan
            });
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = pendingTurnId,
                ConversationId = conversationId,
                UserId = _userId,
                Model = "test/model",
                Status = ConversationTurnStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow, // newest → GetLatestActive returns this one
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await _client.PostAsync(
            $"/api/conversations/{conversationId}/chat/cancel",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ConversationTurnRequests
                .AsNoTracking()
                .SingleAsync(r => r.Id == pendingTurnId, TestContext.Current.CancellationToken);
            row.Status.Should().Be(ConversationTurnStatus.Cancelled,
                "the latest active (Pending) turn is cancelled directly");
        }
    }

    // ── Cancellation: no active turn is an idempotent no-op 202 ────

    [Fact]
    public async Task CancelChat_NoActiveTurn_Returns202()
    {
        var conversationId = await CreateConversationAsync();

        var response = await _client.PostAsync(
            $"/api/conversations/{conversationId}/chat/cancel",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task CancelChat_OtherUsersConversation_Returns404()
    {
        var otherUserId = Guid.NewGuid();
        var persona = await TestPersonas.CreateAsync(
            _factory.Services, otherUserId, ct: TestContext.Current.CancellationToken);
        var conversationId = Guid.NewGuid();
        await SendCommandAs(otherUserId, new CreateConversationCommand(conversationId, otherUserId, persona.Id, "Not Mine"));

        var response = await _client.PostAsync(
            $"/api/conversations/{conversationId}/chat/cancel",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Cancellation: mid-stream flavour (4.4) ────────────────────

    [Fact]
    public async Task CancelChat_ProcessingTurn_FiresCtsAndRecordsTurnCancelled()
    {
        var marker = $"mid-stream-cancel-{Guid.NewGuid()}";
        var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();

        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                var ct = call.ArgAt<CancellationToken>(1);
                if (request.Messages[^1].Content == marker)
                    return GatedStream(streamStarted, release.Task, ct);
                return DefaultStream(ct);
            });

        var conversationId = await CreateConversationAsync();
        await PostChatAsync(conversationId, marker);

        // Wait until the stream is actually in flight (row Processing, CTS registered).
        await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var response = await _client.PostAsync(
            $"/api/conversations/{conversationId}/chat/cancel",
            content: null,
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // The CTS firing makes the gated stream observe cancellation. The
        // orchestrator records TurnCancelled and the row ends Cancelled.
        await WaitUntilAsync(
            async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Cancelled,
            TimeSpan.FromSeconds(10));

        var stream = await LoadStreamAsync(conversationId);
        stream.OfType<TurnCancelled>().Should().ContainSingle();

        // Release in case the stream is still parked (it should have thrown already).
        release.TrySetResult();
    }

    // ── Stream helpers ────────────────────────────────────────────

    private static async IAsyncEnumerable<StreamedChunk> DefaultStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk("ok", false, null);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk(null, true, new UsageInfo(1, 1, 2));
    }

    private static async IAsyncEnumerable<StreamedChunk> GatedStream(
        TaskCompletionSource started,
        Task release,
        [EnumeratorCancellation] CancellationToken ct)
    {
        started.TrySetResult();
        // Block until released OR cancelled. If cancelled, throw so the orchestrator
        // takes its TurnCancelled path.
        await release.WaitAsync(ct);
        yield return new StreamedChunk("done", false, null);
        yield return new StreamedChunk(null, true, new UsageInfo(1, 1, 2));
    }
}
