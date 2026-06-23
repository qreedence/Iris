using Iris.Api.Authentication;
using Iris.Application.Conversations;
using Iris.Application.Identity.Interfaces;

namespace Iris.Api.Conversations;

public class ConversationTurnWorker : BackgroundService
{
    private readonly IConversationTurnQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationTurnWorker> _logger;

    public ConversationTurnWorker(
        IConversationTurnQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationTurnWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var workItem in _queue.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(workItem, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task ProcessAsync(ConversationTurnWorkItem workItem, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var userService = (CurrentUserService)scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
            userService.OverrideUserId = workItem.UserId;
            var orchestrator = scope.ServiceProvider.GetRequiredService<IChatStreamOrchestrator>();

            await orchestrator.StreamAsync(
                workItem.ConversationId,
                workItem.Model,
                workItem.ChangeModel,
                workItem.ModelParameters,
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Conversation turn worker cancelled while processing conversation {ConversationId}",
                workItem.ConversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Background streaming failed for conversation {ConversationId}",
                workItem.ConversationId);

            await SendBestEffortErrorAsync(workItem.ConversationId);
        }
    }

    private async Task SendBestEffortErrorAsync(Guid conversationId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var notifier = scope.ServiceProvider.GetRequiredService<IChatStreamNotifier>();

            await notifier.SendErrorAsync(
                conversationId,
                "internal_error",
                "Streaming failed to start.",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to send background streaming error for conversation {ConversationId}",
                conversationId);
        }
    }
}
