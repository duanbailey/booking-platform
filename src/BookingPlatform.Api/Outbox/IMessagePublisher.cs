namespace BookingPlatform.Api.Outbox;

/// <summary>
/// One outbox row as an SQS FIFO message: the stored envelope (as MySQL returns the JSON column: same content, keys
/// sorted), its message group, and its eventId as the deduplication ID.
/// </summary>
public sealed record OutgoingMessage(Guid EventId, string MessageGroupId, string Body);

/// <summary>The outcome for one message of a batch: <see cref="Error"/> is null when it was accepted.</summary>
public sealed record PublishOutcome(Guid EventId, string? Error);

/// <summary>
/// Publishes to the SQS FIFO queue (ADR 0007: a small interface, an in-memory fake in tests, SQS in production).
/// The relay sends at most one message per message group in a batch (T21), so a partial failure never reorders a
/// group.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>One outcome per message, in any order. Throwing means none of them is known to be accepted.</summary>
    Task<IReadOnlyList<PublishOutcome>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken ct);
}
