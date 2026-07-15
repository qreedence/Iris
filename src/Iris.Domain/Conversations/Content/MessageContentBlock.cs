using Iris.Domain.AiIntegration;

namespace Iris.Domain.Conversations.Content;

public sealed record MessageContentBlock
{
    public required ContentBlockType Type { get; init; }

    public string? Content { get; init; }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? ProviderMetadata { get; init; }

    public Guid? UploadId { get; init; }

    public string? ToolCallId { get; init; }

    public string? Name { get; init; }

    public string? ArgumentsJson { get; init; }

    public Guid? PayloadId { get; init; }

    public ToolExecutionStatus? Status { get; init; }

    public long? DurationMs { get; init; }

    public static MessageContentBlock Text(string content)
    {
        return new MessageContentBlock
        {
            Type = ContentBlockType.Text,
            Content = content,
        };
    }

    public static MessageContentBlock Thinking(
        string content,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? providerMetadata = null)
    {
        return new MessageContentBlock
        {
            Type = ContentBlockType.Thinking,
            Content = content,
            ProviderMetadata = providerMetadata,
        };
    }

    public static MessageContentBlock Image(Guid uploadId)
    {
        return new MessageContentBlock
        {
            Type = ContentBlockType.Image,
            UploadId = uploadId,
        };
    }

    public static MessageContentBlock ToolUse(
        string toolCallId,
        string name,
        string argumentsJson,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? providerMetadata = null)
    {
        return new MessageContentBlock
        {
            Type = ContentBlockType.ToolUse,
            ToolCallId = toolCallId,
            Name = name,
            ArgumentsJson = argumentsJson,
            ProviderMetadata = providerMetadata,
        };
    }

    public static MessageContentBlock ToolResult(
        string toolCallId,
        Guid payloadId,
        string? name = null,
        string? preview = null,
        ToolExecutionStatus? status = null,
        long? durationMs = null)
    {
        return new MessageContentBlock
        {
            Type = ContentBlockType.ToolResult,
            ToolCallId = toolCallId,
            PayloadId = payloadId,
            Name = name,
            Content = preview,
            Status = status,
            DurationMs = durationMs,
        };
    }
}
