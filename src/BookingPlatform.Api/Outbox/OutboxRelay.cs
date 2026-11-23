using System.Data;
using BookingPlatform.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Outbox;

/// <summary>
/// The outbox relay (ADR 0008, T21): publishes committed outbox rows to the SQS FIFO queue and marks them published.
/// Several API instances run it at once. Decisions 2026-11-23 (plan/tasks.md, T21 and "Outbox ordering"):
/// <list type="bullet">
/// <item>Each message group is published in outbox.seq order. A round claims, FOR UPDATE SKIP LOCKED, up to
/// <see cref="OutboxOptions.BatchSize"/> rows in seq order, each the earliest unpublished row of its group and not
/// waiting for a retry. So a group's later row is never sent while an earlier one is unpublished, whether another
/// instance holds it or it is backing off, and a batch never holds two rows of one group, so a partial failure can't
/// reorder a group.</item>
/// <item>The row locks are held while sending, and published_at is set in the same transaction. A crash after
/// sending republishes the row with the same deduplication ID (at least once).</item>
/// <item>A failed row is retried forever with backoff (1 s doubling to 60 s), its attempts and last error kept on
/// the row and logged as an error; it holds back only its own group. Nothing is skipped.</item>
/// </list>
/// The round runs on its own connection at READ COMMITTED, never inside a request's transaction (ADR 0002).
/// </summary>
public sealed class OutboxRelay(IServiceScopeFactory scopes, IMessagePublisher publisher, TimeProvider time, ILogger<OutboxRelay> logger)
{
    private sealed record ClaimedRow(Guid Id, string MessageGroupId, string Payload, int Attempts);

    /// <summary>Runs one round and returns how many rows it published.</summary>
    public async Task<int> RunRoundAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        // READ COMMITTED: each statement sees rows committed up to then, and no gap locks are taken.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var now = Now();

        // The NOT EXISTS subquery is a plain (non-locking) read, so it sees an earlier row of the group even while
        // another instance has it locked, and keeps this group's later rows back.
        var claimed = await db.Database.SqlQuery<ClaimedRow>($"""
            SELECT o.id AS Id, o.message_group_id AS MessageGroupId, o.payload AS Payload, o.attempts AS Attempts
            FROM outbox o
            WHERE o.published_at IS NULL
              AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= {now})
              AND NOT EXISTS (
                SELECT 1 FROM outbox e
                WHERE e.message_group_id = o.message_group_id AND e.published_at IS NULL AND e.seq < o.seq)
            ORDER BY o.seq
            LIMIT {OutboxOptions.BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (claimed.Count == 0) return 0;

        var outcomes = await PublishAsync(claimed, ct);
        var published = new List<Guid>();
        foreach (var row in claimed)
        {
            var error = outcomes.TryGetValue(row.Id, out var outcome) ? outcome.Error : "The publisher returned no outcome for this message.";
            if (error is null)
            {
                published.Add(row.Id);
                continue;
            }

            var attempt = row.Attempts + 1;
            var nextAttemptAt = now + RetryDelay(attempt);
            var lastError = error.Length <= OutboxMessageLimits.LastErrorLength ? error : error[..OutboxMessageLimits.LastErrorLength];
            await db.Outbox.Where(o => o.Id == row.Id).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Attempts, attempt)
                .SetProperty(o => o.LastError, lastError)
                .SetProperty(o => o.NextAttemptAt, nextAttemptAt), ct);
            // Never the payload: events carry no personal data, but logs shouldn't carry event contents either.
            logger.LogError("Publishing outbox event {EventId} (message group {MessageGroupId}) failed on attempt {Attempt}; retrying at {NextAttemptAt:O}: {Error}",
                row.Id, row.MessageGroupId, attempt, nextAttemptAt, lastError);
        }
        if (published.Count > 0)
        {
            await db.Outbox.Where(o => published.Contains(o.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.PublishedAt, now)
                .SetProperty(o => o.LastError, (string?)null), ct);
        }
        await transaction.CommitAsync(ct);

        logger.LogDebug("Published {Count} outbox event(s)", published.Count);
        return published.Count;
    }

    /// <summary>1 s after the first failure, doubling per failure, at most 60 s.</summary>
    public static TimeSpan RetryDelay(int attempt)
    {
        var seconds = OutboxOptions.FirstRetryDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        return TimeSpan.FromSeconds(Math.Min(seconds, OutboxOptions.MaxRetryDelay.TotalSeconds));
    }

    /// <summary>The publisher's outcomes by event ID; a call that throws fails every message in it.</summary>
    private async Task<Dictionary<Guid, PublishOutcome>> PublishAsync(List<ClaimedRow> claimed, CancellationToken ct)
    {
        var batch = claimed.Select(r => new OutgoingMessage(r.Id, r.MessageGroupId, r.Payload)).ToList();
        try
        {
            var outcomes = await publisher.PublishAsync(batch, ct);
            return outcomes.GroupBy(o => o.EventId).ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var error = $"{e.GetType().Name}: {e.Message}";
            return claimed.ToDictionary(r => r.Id, r => new PublishOutcome(r.Id, error));
        }
    }

    /// <summary>datetime(6): microseconds, so the stored value is exactly the one compared and logged.</summary>
    private DateTime Now()
    {
        var now = time.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % 10));
    }
}
