using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using Iris.Tests.Integration.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Iris.Domain.Conversations.Content;

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

    private async Task<Guid> CreatePersonaAsync(string? role = null)
    {
        var persona = await TestPersonas.CreateAsync(
            _factory.Services, _userId, role: role, ct: TestContext.Current.CancellationToken);
        return persona.Id;
    }

    private async Task<Guid> CreateConversationAsync(string? personaRole = null)
    {
        var personaId = await CreatePersonaAsync(personaRole);
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
                var last = request.Messages[^1].VisibleText;
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
                if (request.Messages.Any(m => m.VisibleText == marker1))
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
                    new MessageSent(message1Id, conversationId, MessageContentBlocks.Text(marker1), Iris.Domain.AiIntegration.ChatRole.User),
                    new AssistantResponseCompleted(Guid.NewGuid(), conversationId, message1Id, MessageContentBlocks.Text("turn 1 response"), "test/model", FinishReason.Stop),
                    new TurnCompleted(conversationId, message1Id, 1, 1),
                    new MessageSent(message2Id, conversationId, MessageContentBlocks.Text(marker2), Iris.Domain.AiIntegration.ChatRole.User),
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

    [Fact]
    public async Task Retry_WithRecordedCancellation_MarksRowCancelledWithoutStreaming()
    {
        var marker = $"idem-cancelled-{Guid.NewGuid()}";
        var providerCalls = 0;
        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.Arg<ChatRequest>().Messages.Any(m => m.VisibleText == marker))
                    Interlocked.Increment(ref providerCalls);
                return DefaultStream(call.ArgAt<CancellationToken>(1));
            });

        var conversationId = await CreateConversationAsync();
        var messageId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentUserService>().OverrideUserId = _userId;
            var recorder = scope.ServiceProvider.GetRequiredService<IConversationEventRecorder>();
            await recorder.RecordAsync(
                conversationId,
                [
                    new MessageSent(messageId, conversationId, MessageContentBlocks.Text(marker), ChatRole.User),
                    new TurnCancelled(conversationId, null, messageId),
                ],
                TestContext.Current.CancellationToken);
        }
        await SeedStaleTurnAsync(conversationId, messageId);

        await WaitUntilAsync(
            async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Cancelled,
            TimeSpan.FromSeconds(20));

        providerCalls.Should().Be(0);
    }

    [Fact]
    public async Task Retry_AfterToolCallRound_ExecutesMissingToolAndCompletes()
    {
        var marker = $"resume-missing-tool-{Guid.NewGuid()}";
        var toolCallId = $"call-{Guid.NewGuid()}";
        var providerCalls = 0;

        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                if (request.Messages.Any(m => m.VisibleText == marker))
                {
                    Interlocked.Increment(ref providerCalls);
                    request.Messages.Should().Contain(m =>
                        m.Role == ChatRole.Tool
                        && m.ContentBlocks.Any(b => b.ToolCallId == toolCallId)
                        && m.ToolResultContent != null);
                }

                return DefaultStream(call.ArgAt<CancellationToken>(1));
            });

        var conversationId = await CreateConversationAsync("orchestrator");
        var messageId = Guid.NewGuid();
        await SeedToolRoundAsync(conversationId, messageId, marker, toolCallId);
        await SeedStaleTurnAsync(conversationId, messageId);

        await WaitUntilAsync(
            async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Completed,
            TimeSpan.FromSeconds(20));

        providerCalls.Should().Be(1, "the intermediate tool-call round must resume rather than look terminal");

        var stream = await LoadStreamAsync(conversationId);
        stream.OfType<ToolExecuted>().Should().ContainSingle(e => e.ToolCallId == toolCallId);
        stream.OfType<AssistantResponseCompleted>()
            .Should().ContainSingle(e => e.MessageId == messageId && e.FinishReason == FinishReason.Stop);
        stream.OfType<TurnCompleted>().Should().ContainSingle(e => e.MessageId == messageId);
    }

    [Fact]
    public async Task Retry_WithRecordedToolResult_SkipsReExecutionAndCompletes()
    {
        var marker = $"resume-recorded-tool-{Guid.NewGuid()}";
        var toolCallId = $"call-{Guid.NewGuid()}";
        var providerCalls = 0;

        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                if (request.Messages.Any(m => m.VisibleText == marker))
                {
                    Interlocked.Increment(ref providerCalls);
                    request.Messages.Should().Contain(m =>
                        m.Role == ChatRole.Tool
                        && m.ContentBlocks.Any(b => b.ToolCallId == toolCallId)
                        && m.ToolResultContent == "{\"already\":true}");
                }

                return DefaultStream(call.ArgAt<CancellationToken>(1));
            });

        var conversationId = await CreateConversationAsync("orchestrator");
        var messageId = Guid.NewGuid();
        await SeedToolRoundAsync(conversationId, messageId, marker, toolCallId);

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentUserService>().OverrideUserId = _userId;
            var recorder = scope.ServiceProvider.GetRequiredService<IToolExecutionRecorder>();
            await recorder.RecordAsync(
                conversationId,
                messageId,
                new ToolCall(toolCallId, "get_current_time", "{}"),
                new ToolResult("{\"already\":true}", "already recorded", ToolExecutionStatus.Succeeded),
                4,
                TestContext.Current.CancellationToken);
        }

        await SeedStaleTurnAsync(conversationId, messageId);

        await WaitUntilAsync(
            async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Completed,
            TimeSpan.FromSeconds(20));

        providerCalls.Should().Be(1);

        var stream = await LoadStreamAsync(conversationId);
        stream.OfType<ToolExecuted>().Should().ContainSingle(e => e.ToolCallId == toolCallId);

        using var verificationScope = _factory.Services.CreateScope();
        var payloadCount = await verificationScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ToolResultPayloads
            .CountAsync(
                p => p.ConversationId == conversationId && p.ToolCallId == toolCallId,
                TestContext.Current.CancellationToken);
        payloadCount.Should().Be(1, "an already durable tool result must not be executed or stored twice");
    }

    [Fact]
    public async Task PostChat_ToolRound_CompletesAndRebuildsNextTurnContext()
    {
        var firstMarker = $"e2e-tool-{Guid.NewGuid()}";
        var secondMarker = $"e2e-followup-{Guid.NewGuid()}";
        var toolCallId = $"call-{Guid.NewGuid()}";
        ChatRequest? nextTurnRequest = null;

        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                var ct = call.ArgAt<CancellationToken>(1);

                if (request.Messages.Any(m => m.VisibleText == secondMarker))
                {
                    nextTurnRequest = request;
                    return DefaultStream(ct);
                }

                if (request.Messages.Any(m => m.VisibleText == firstMarker)
                    && request.Messages.All(m => m.Role != ChatRole.Tool))
                {
                    return ToolCallStream(toolCallId, ct);
                }

                return DefaultStream(ct);
            });

        var conversationId = await CreateConversationAsync("orchestrator");
        await PostChatAsync(conversationId, firstMarker);

        await WaitUntilAsync(
            async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Completed,
            TimeSpan.FromSeconds(20));

        var firstTurnStream = await LoadStreamAsync(conversationId);
        var firstMessage = firstTurnStream.OfType<MessageSent>().Single(m =>
            MessageContentBlocks.ToVisibleText(m.ContentBlocks) == firstMarker);
        firstTurnStream
            .SkipWhile(e => e != firstMessage)
            .Select(e => e.GetType())
            .Should().Equal(
                typeof(MessageSent),
                typeof(AssistantResponseCompleted),
                typeof(ToolExecuted),
                typeof(AssistantResponseCompleted),
                typeof(TurnCompleted));

        var rounds = firstTurnStream.OfType<AssistantResponseCompleted>()
            .Where(e => e.MessageId == firstMessage.Id)
            .ToList();
        rounds.Select(e => e.FinishReason).Should().Equal(FinishReason.ToolCalls, FinishReason.Stop);

        using (var scope = _factory.Services.CreateScope())
        {
            var messages = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .ConversationMessages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(TestContext.Current.CancellationToken);

            messages.Select(m => m.Role)
                .Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
            messages[1].ContentBlocks.Should().ContainSingle(b => b.Type == ContentBlockType.ToolUse);
            messages[2].ContentBlocks.Should().ContainSingle(b => b.Type == ContentBlockType.ToolResult);
        }

        await PostChatAsync(conversationId, secondMarker);
        await WaitUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .ConversationTurnRequests
                .CountAsync(
                    row => row.ConversationId == conversationId
                        && row.Status == ConversationTurnStatus.Completed,
                    TestContext.Current.CancellationToken) == 2;
        }, TimeSpan.FromSeconds(20));

        nextTurnRequest.Should().NotBeNull();
        nextTurnRequest!.Messages.Select(m => m.Role)
            .Should().Equal(
                ChatRole.User,
                ChatRole.Assistant,
                ChatRole.Tool,
                ChatRole.Assistant,
                ChatRole.User);
        nextTurnRequest.Messages[2].ToolResultContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CancelChat_AfterToolExecution_KeepsCompletedRoundAndCancelsLoop()
    {
        var marker = $"e2e-tool-cancel-{Guid.NewGuid()}";
        var toolCallId = $"call-{Guid.NewGuid()}";
        var followUpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();

        _factory.MockChatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ChatRequest>();
                var ct = call.ArgAt<CancellationToken>(1);

                if (!request.Messages.Any(m => m.VisibleText == marker))
                    return DefaultStream(ct);

                return request.Messages.Any(m => m.Role == ChatRole.Tool)
                    ? GatedStream(followUpStarted, release.Task, ct)
                    : ToolCallStream(toolCallId, ct);
            });

        var conversationId = await CreateConversationAsync("orchestrator");
        await PostChatAsync(conversationId, marker);
        await followUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var response = await _client.PostAsync(
            $"/api/conversations/{conversationId}/chat/cancel",
            content: null,
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await WaitUntilAsync(
            async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Cancelled,
            TimeSpan.FromSeconds(10));

        var stream = await LoadStreamAsync(conversationId);
        var messageId = stream.OfType<MessageSent>()
            .Single(m => MessageContentBlocks.ToVisibleText(m.ContentBlocks) == marker)
            .Id;

        stream.OfType<AssistantResponseCompleted>()
            .Should().ContainSingle(e => e.MessageId == messageId && e.FinishReason == FinishReason.ToolCalls);
        stream.OfType<ToolExecuted>().Should().ContainSingle(e => e.MessageId == messageId);
        stream.OfType<TurnCancelled>().Should().ContainSingle(e => e.MessageId == messageId);
        stream.OfType<TurnCompleted>().Should().NotContain(e => e.MessageId == messageId);

        release.TrySetResult();
    }

    private async Task SeedToolRoundAsync(
        Guid conversationId,
        Guid messageId,
        string marker,
        string toolCallId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUserService>().OverrideUserId = _userId;
        var recorder = scope.ServiceProvider.GetRequiredService<IConversationEventRecorder>();
        await recorder.RecordAsync(
            conversationId,
            [
                new MessageSent(messageId, conversationId, MessageContentBlocks.Text(marker), ChatRole.User),
                new AssistantResponseCompleted(
                    Guid.NewGuid(),
                    conversationId,
                    messageId,
                    [MessageContentBlock.ToolUse(toolCallId, "get_current_time", "{}")],
                    "test/model",
                    FinishReason.ToolCalls,
                    3,
                    2),
            ],
            TestContext.Current.CancellationToken);
    }

    private async Task SeedStaleTurnAsync(Guid conversationId, Guid messageId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ConversationTurnRequests.Add(new ConversationTurnRequest
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = _userId,
            MessageId = messageId,
            Model = "test/model",
            Status = ConversationTurnStatus.Processing,
            AttemptCount = 1,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ── Startup orphan recovery ignores the lease ─────────────────

    [Fact]
    public async Task WorkerStartup_FreshProcessingRow_RecoveredImmediatelyWithoutWaitingOutLease()
    {
        // Deploy-mid-stream scenario: the old process died leaving a Processing row
        // with a RECENT ClaimedAt. A restarted worker must not wait out the full
        // ClaimLease — its first tick treats every Processing row as ownerless
        // (registry empty, nothing claimed yet) and recovers it immediately. This
        // test simulates the restart by starting a SECOND worker instance with a
        // fresh (empty) registry and doorbell.
        var marker = $"startup-recovery-{Guid.NewGuid()}";
        var conversationId = await CreateConversationAsync();

        var messageId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
            userService.OverrideUserId = _userId;
            var recorder = scope.ServiceProvider.GetRequiredService<IConversationEventRecorder>();
            await recorder.RecordAsync(
                conversationId,
                [new MessageSent(messageId, conversationId, MessageContentBlocks.Text(marker), Iris.Domain.AiIntegration.ChatRole.User)],
                TestContext.Current.CancellationToken);
        }

        var turnId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ConversationTurnRequests.Add(new ConversationTurnRequest
            {
                Id = turnId,
                ConversationId = conversationId,
                UserId = _userId,
                MessageId = messageId,
                Model = "test/model",
                Status = ConversationTurnStatus.Processing,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
                ClaimedAt = DateTimeOffset.UtcNow, // FRESH — well within the lease
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The app's own worker (long past its first tick) respects the lease and will
        // NOT recover this fresh row — only the restarted worker's first tick can.
        var restartedWorker = new Iris.Api.Conversations.ConversationTurnWorker(
            new Iris.Api.Conversations.TurnDoorbell(),
            new Iris.Api.Conversations.ActiveTurnRegistry(),
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new Iris.Api.Conversations.TurnProcessingOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(100),
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Iris.Api.Conversations.ConversationTurnWorker>.Instance);

        await restartedWorker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // First tick: zero-lease recovery resets the row to Pending; it is then
            // reclaimed and re-streamed to completion — the turn RESUMES immediately.
            await WaitUntilAsync(
                async () => await GetTurnStatusAsync(conversationId) == ConversationTurnStatus.Completed,
                TimeSpan.FromSeconds(10));

            // Guard the other way: past the first tick, the lease is respected — a
            // fresh Processing row seeded NOW must NOT be recovered mid-run.
            var guardRowId = Guid.NewGuid();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.ConversationTurnRequests.Add(new ConversationTurnRequest
                {
                    Id = guardRowId,
                    ConversationId = Guid.NewGuid(),
                    UserId = _userId,
                    Model = "test/model",
                    Status = ConversationTurnStatus.Processing,
                    AttemptCount = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ClaimedAt = DateTimeOffset.UtcNow, // fresh — within the lease
                });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            // Several 100ms poll ticks elapse; the row must remain untouched.
            await Task.Delay(500, TestContext.Current.CancellationToken);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var guardRow = await db.ConversationTurnRequests
                    .AsNoTracking()
                    .SingleAsync(r => r.Id == guardRowId, TestContext.Current.CancellationToken);
                guardRow.Status.Should().Be(ConversationTurnStatus.Processing,
                    "after the first tick the lease is respected; mid-run recovery must not touch a fresh claim");
            }
        }
        finally
        {
            await restartedWorker.StopAsync(CancellationToken.None);
        }
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
                if (request.Messages[^1].VisibleText == marker)
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

    private static IAsyncEnumerable<StreamedChunk> DefaultStream(CancellationToken ct) =>
        ChatProviderMock.DefaultStream(ct);

    private static async IAsyncEnumerable<StreamedChunk> ToolCallStream(
        string toolCallId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk(
            null,
            true,
            new UsageInfo(3, 2, 5),
            ToolCalls: [new ToolCall(toolCallId, "get_current_time", "{}", $"fc-{toolCallId}")],
            FinishReason: FinishReason.ToolCalls);
        await Task.CompletedTask;
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
