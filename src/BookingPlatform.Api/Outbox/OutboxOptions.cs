namespace BookingPlatform.Api.Outbox;

/// <summary>The relay's settings (section "Outbox"). Decided 2026-11-23 (plan/tasks.md, T21).</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>The SQS FIFO queue URL. Without it the relay doesn't run in Development; elsewhere startup fails.</summary>
    public string? QueueUrl { get; set; }

    /// <summary>How long the relay waits after a round that published nothing (ADR 0008: 1-2 s).</summary>
    public int PollIntervalMilliseconds { get; set; } = 1000;

    /// <summary>The least time between two outbox-age metric lines (T27a, <see cref="OutboxAgeMetric"/>).</summary>
    public int MetricIntervalSeconds { get; set; } = 60;

    /// <summary>Rows per round: SQS's SendMessageBatch maximum.</summary>
    public const int BatchSize = 10;

    /// <summary>The first retry delay after a failed publish; it doubles per failure up to <see cref="MaxRetryDelay"/>.</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
}
