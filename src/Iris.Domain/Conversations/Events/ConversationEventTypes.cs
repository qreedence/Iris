namespace Iris.Domain.Conversations.Events;

/// <summary>
/// THE single registry of all known <see cref="ConversationEvent"/> subtypes. Adding a
/// 9th event type means adding it here and nowhere else in terms of "is this a known
/// event" — the event store (write-time guard + read-time deserialization lookup) and
/// the event recorder (notification dispatch) both key off <see cref="ByName"/>.
/// </summary>
public static class ConversationEventTypes
{
    public static IReadOnlyDictionary<string, Type> ByName { get; } = new Dictionary<string, Type>
    {
        [nameof(ConversationCreated)] = typeof(ConversationCreated),
        [nameof(MessageSent)] = typeof(MessageSent),
        [nameof(AssistantResponseCompleted)] = typeof(AssistantResponseCompleted),
        [nameof(ToolExecuted)] = typeof(ToolExecuted),
        [nameof(TurnCompleted)] = typeof(TurnCompleted),
        [nameof(TurnFailed)] = typeof(TurnFailed),
        [nameof(TurnCancelled)] = typeof(TurnCancelled),
        [nameof(ModelChanged)] = typeof(ModelChanged),
        [nameof(ConversationArchived)] = typeof(ConversationArchived),
    };
}
