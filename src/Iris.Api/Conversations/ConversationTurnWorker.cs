using System.Text.Json;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Application.Identity.Interfaces;
using Iris.Domain.Conversations.Entities;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Options;

namespace Iris.Api.Conversations;

public class ConversationTurnWorker : BackgroundService
{
    private readonly ITurnDoorbell _doorbell;
    private readonly IActiveTurnRegistry _activeTurns;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TurnProcessingOptions _options;
    private readonly ILogger<ConversationTurnWorker> _logger;

    private readonly SemaphoreSlim _concurrency;
    private int _runningCount;

    public ConversationTurnWorker(
        ITurnDoorbell doorbell,
        IActiveTurnRegistry activeTurns,
        IServiceScopeFactory scopeFactory,
        IOptions<TurnProcessingOptions> options,
        ILogger<ConversationTurnWorker> logger)
    {
        _doorbell = doorbell;
        _activeTurns = activeTurns;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentTurns, _options.MaxConcurrentTurns);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverOrphansAsync(stoppingToken);
                await ClaimAndDispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Harden the loop: a failed claim/recovery round must never kill
                // the BackgroundService. Log and carry on to the next tick.
                _logger.LogError(ex, "Conversation turn worker loop iteration failed");
            }

            try
            {
                await WaitForWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        // Wake on either a doorbell ring (new turn enqueued) or the poll interval
        // (catches orphans, retries, and rows written outside this process).
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var doorbellWait = _doorbell.WaitAsync(pollCts.Token);
        var pollWait = Task.Delay(_options.PollInterval, pollCts.Token);

        await Task.WhenAny(doorbellWait, pollWait);
        pollCts.Cancel();

        // Observe faults from the losing task without letting cancellation surface.
        await SwallowAsync(doorbellWait);
        await SwallowAsync(pollWait);
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected: the losing branch was cancelled.
        }
    }

    private async Task RecoverOrphansAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationTurnRequestStore>();

        var failed = await store.RecoverOrphansAsync(_options.ClaimLease, _options.MaxAttempts, stoppingToken);

        foreach (var orphan in failed)
        {
            await RecordTurnFailedAsync(scope.ServiceProvider, orphan, stoppingToken);
        }
    }

    private async Task ClaimAndDispatchAsync(CancellationToken stoppingToken)
    {
        var capacity = _options.MaxConcurrentTurns - Volatile.Read(ref _runningCount);
        if (capacity <= 0)
            return;

        IReadOnlyList<ConversationTurnRequest> claimed;
        using (var scope = _scopeFactory.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversationTurnRequestStore>();
            claimed = await store.ClaimPendingAsync(capacity, _options.MaxAttempts, stoppingToken);
        }

        foreach (var request in claimed)
        {
            await _concurrency.WaitAsync(stoppingToken);
            Interlocked.Increment(ref _runningCount);

            // Fire-and-track: each turn runs on its own task with its own scope.
            _ = Task.Run(() => DispatchAsync(request, stoppingToken), CancellationToken.None);
        }
    }

    private async Task DispatchAsync(ConversationTurnRequest request, CancellationToken stoppingToken)
    {
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeTurns.Register(request.ConversationId, turnCts);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
            userService.OverrideUserId = request.UserId;

            // Idempotency: on a retry the previous attempt may have completed the
            // stream but crashed before marking the row. Skip if a terminal event
            // already exists so we never double-spend tokens.
            if (request.AttemptCount > 1
                && await TurnAlreadyTerminalAsync(scope.ServiceProvider, request.ConversationId, stoppingToken))
            {
                await UpdateStatusAsync(store => store.MarkCompletedAsync(request.Id, stoppingToken));
                return;
            }

            var orchestrator = scope.ServiceProvider.GetRequiredService<IChatStreamOrchestrator>();

            await orchestrator.StreamAsync(
                request.UserId,
                request.ConversationId,
                request.Model,
                request.ChangeModel,
                Deserialize(request.ModelParameters),
                turnCts.Token);

            // StreamAsync returns normally for success AND for user-cancellation
            // (the orchestrator records TurnCancelled internally and returns). If
            // the user cancelled mid-stream, mark the row Cancelled; otherwise the
            // turn completed.
            if (turnCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                await UpdateStatusAsync(store => store.MarkCancelledAsync(request.Id, CancellationToken.None));
            }
            else
            {
                await UpdateStatusAsync(store => store.MarkCompletedAsync(request.Id, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: leave the row Processing. Orphan recovery will reset it
            // to Pending-eligible on a future run — NOT a user cancel or failure.
            _logger.LogInformation(
                "Turn {TurnId} for conversation {ConversationId} interrupted by host shutdown; left for orphan recovery",
                request.Id,
                request.ConversationId);
        }
        catch (Exception ex)
        {
            await HandleUnexpectedFailureAsync(request, ex);
        }
        finally
        {
            _activeTurns.Remove(request.ConversationId);
            Interlocked.Decrement(ref _runningCount);
            _concurrency.Release();
        }
    }

    private async Task HandleUnexpectedFailureAsync(ConversationTurnRequest request, Exception ex)
    {
        try
        {
            if (request.AttemptCount < _options.MaxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Turn {TurnId} for conversation {ConversationId} failed on attempt {Attempt}; will retry",
                    request.Id,
                    request.ConversationId,
                    request.AttemptCount);

                await UpdateStatusAsync(store => store.ResetToPendingAsync(request.Id, ex.Message, CancellationToken.None));
                _doorbell.Ring();
            }
            else
            {
                _logger.LogError(
                    ex,
                    "Turn {TurnId} for conversation {ConversationId} failed after {Attempt} attempts; marking Failed",
                    request.Id,
                    request.ConversationId,
                    request.AttemptCount);

                await UpdateStatusAsync(store => store.MarkFailedAsync(request.Id, ex.Message, CancellationToken.None));
                await RecordInterruptedFailureAsync(request);
            }
        }
        catch (Exception updateEx)
        {
            _logger.LogError(
                updateEx,
                "Failed to update status for turn {TurnId} after processing failure",
                request.Id);
        }
    }

    private async Task RecordInterruptedFailureAsync(ConversationTurnRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        userService.OverrideUserId = request.UserId;
        await RecordTurnFailedAsync(scope.ServiceProvider, request, CancellationToken.None);
    }

    private async Task RecordTurnFailedAsync(
        IServiceProvider provider,
        ConversationTurnRequest request,
        CancellationToken ct)
    {
        try
        {
            var userService = provider.GetRequiredService<ICurrentUserService>();
            userService.OverrideUserId = request.UserId;

            var recorder = provider.GetRequiredService<IConversationEventRecorder>();
            var turnFailed = new TurnFailed(
                request.ConversationId,
                FailureSource.Internal,
                "interrupted",
                "The turn was interrupted and could not be completed.",
                PartialContent: null);

            await recorder.RecordAsync(request.ConversationId, [turnFailed], ct);

            var notifier = provider.GetRequiredService<IChatStreamNotifier>();
            await notifier.SendErrorAsync(
                request.ConversationId,
                "interrupted",
                "The turn was interrupted and could not be completed.",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to record TurnFailed for interrupted turn {TurnId} (conversation {ConversationId})",
                request.Id,
                request.ConversationId);
        }
    }

    private async Task<bool> TurnAlreadyTerminalAsync(
        IServiceProvider provider,
        Guid conversationId,
        CancellationToken ct)
    {
        var eventStore = provider.GetRequiredService<IEventStore>();
        var stream = await eventStore.LoadStreamAsync(conversationId, ct);

        var lastUserMessageIndex = -1;
        for (var i = stream.Count - 1; i >= 0; i--)
        {
            if (stream[i] is MessageSent)
            {
                lastUserMessageIndex = i;
                break;
            }
        }

        if (lastUserMessageIndex < 0)
            return false;

        for (var i = lastUserMessageIndex + 1; i < stream.Count; i++)
        {
            if (stream[i] is TurnCompleted or TurnFailed or TurnCancelled)
                return true;
        }

        return false;
    }

    private async Task UpdateStatusAsync(Func<IConversationTurnRequestStore, Task> update)
    {
        // Each row-status update uses its own short-lived scope; never share a
        // DbContext across parallel tasks.
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationTurnRequestStore>();
        await update(store);
    }

    private static ModelParameters? Deserialize(string? json)
    {
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ModelParameters>(json);
    }
}
