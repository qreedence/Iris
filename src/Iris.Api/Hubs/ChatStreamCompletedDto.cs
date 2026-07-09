namespace Iris.Api.Hubs;

/// <summary>
/// Completion payload for SignalR streaming. Carries the conversation identity
/// so clients can finalize the correct conversation's stream even when the user
/// has navigated away mid-stream.
/// </summary>
public record ChatStreamCompletedDto(Guid ConversationId);
