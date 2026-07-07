namespace Iris.Domain.Conversations.Content;

public class Upload
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public UploadStatus Status { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? OriginalFileName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
