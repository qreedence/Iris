namespace Iris.Api.Hubs;

/// <summary>
/// Error payload for SignalR streaming. Carries the conversation identity so
/// clients can route the failure to the correct conversation's state even when
/// the user has navigated away mid-stream.
/// </summary>
public record ChatStreamErrorDto(
    Guid ConversationId,
    string ErrorCode,
    string Message);
