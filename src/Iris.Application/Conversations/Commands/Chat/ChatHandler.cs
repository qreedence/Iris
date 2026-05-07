using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations.Commands.Chat;
using Iris.Application.Conversations.Notifications;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using MediatR;

namespace Iris.Application.Conversations.Commands.Chat
{
    // CONTEXT WINDOW STRATEGY (M1):
    // Currently loads ALL events from the conversation stream. No truncation.
    // For M1 this is acceptable — conversations are short-lived.
    //
    // Future approach (M2/M3):
    // - Sliding window: keep last N messages + system prompt
    // - Summarization: compress older messages into a summary via the memory system
    // - Token counting: track cumulative tokens per conversation, truncate when approaching model limit
    // - The event store makes this easy — just load fewer events or use a projection with pre-computed summaries
    //
    // See ADR-003 for the full compaction roadmap.

    public class ChatHandler : IRequestHandler<ChatCommand, ChatResponse>
    {
        private readonly IEventStore _eventStore;
        private readonly IPublisher _publisher;
        private readonly IChatProvider _chatProvider;

        public ChatHandler(IEventStore eventStore, IPublisher publisher, IChatProvider chatProvider)
        {
            _eventStore = eventStore;
            _publisher = publisher;
            _chatProvider = chatProvider;
        }

        public async Task<ChatResponse> Handle(ChatCommand command, CancellationToken ct)
        {
            if (command.ConversationId == Guid.Empty)
                throw new ValidationException("ConversationId can not be empty");

            if (string.IsNullOrWhiteSpace(command.UserMessage))
                throw new ValidationException("User message can not be empty");

            var events = await _eventStore.LoadStreamAsync(command.ConversationId, ct);
            if (events.Count == 0)
                throw new NotFoundException("Conversation does not exist.");

            var userMessage = new MessageSent(Guid.NewGuid(), command.ConversationId, command.UserMessage, ChatRole.User);
            await _eventStore.AppendAsync(command.ConversationId, [userMessage], Guid.NewGuid(), ct);
            await _publisher.Publish(new EventNotification<MessageSent>(userMessage, DateTimeOffset.UtcNow), ct);

            var conversationHistory = new List<ChatMessage>();

            foreach (var evt in events)
            {
                if (evt is MessageSent msg)
                    conversationHistory.Add(new ChatMessage(msg.Role, msg.Content));
                else if (evt is AssistantResponseCompleted resp)
                    conversationHistory.Add(new ChatMessage(ChatRole.Assistant, resp.Content));
            }
            conversationHistory.Add(new ChatMessage(ChatRole.User, command.UserMessage));

            var chatRequest = new ChatRequest(Model: command.Model, Messages: conversationHistory, SystemPrompt: command.SystemPrompt, ModelParameters: command.ModelParameters);
            var result = await _chatProvider.CompleteAsync(chatRequest, ct); 

            var assistantResponseCompleted = new AssistantResponseCompleted(Guid.NewGuid(), command.ConversationId, result.Content, chatRequest.Model);

            var inputTokens = result.UsageInfo?.InputTokens ?? 0;
            var outputTokens = result.UsageInfo?.OutputTokens ?? 0;
            var turnCompleted = new TurnCompleted(command.ConversationId, inputTokens, outputTokens);

            await _eventStore.AppendAsync(command.ConversationId, [assistantResponseCompleted, turnCompleted], Guid.NewGuid(), ct);

            await _publisher.Publish(new EventNotification<AssistantResponseCompleted>(assistantResponseCompleted, DateTimeOffset.UtcNow), ct);
            await _publisher.Publish(new EventNotification<TurnCompleted>(turnCompleted, DateTimeOffset.UtcNow), ct);

            return new ChatResponse(result.Content, result.UsageInfo);
        }
    }
}
