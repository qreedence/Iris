namespace Iris.Api.Conversations;

public class TurnProcessingOptions
{
    public const string SectionName = "TurnProcessing";

    /// <summary>Maximum turns streamed concurrently across all conversations.</summary>
    public int MaxConcurrentTurns { get; set; } = 8;

    /// <summary>How long the worker waits between poll ticks when idle.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a Processing row may sit before it is considered orphaned (its
    /// worker crashed / host was killed) and reset for retry.
    /// </summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of attempts before a turn is marked Failed.</summary>
    public int MaxAttempts { get; set; } = 2;
}
