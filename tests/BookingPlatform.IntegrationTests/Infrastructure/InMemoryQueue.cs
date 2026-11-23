using System.Collections.Concurrent;
using BookingPlatform.Api.Outbox;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// The in-memory stand-in for the SQS FIFO queue (ADR 0007). It models what the relay relies on: each message keeps
/// its group, and a repeated deduplication ID (the eventId) is accepted but not delivered again, as SQS does within
/// its five-minute window. Tests can make chosen messages fail, make a whole call throw, accept a batch and then
/// throw (a crash after publishing), or hold a call open to test concurrency.
/// </summary>
public sealed class InMemoryQueue : IMessagePublisher
{
    private readonly ConcurrentQueue<OutgoingMessage> _delivered = new();
    private readonly ConcurrentDictionary<Guid, bool> _seen = new();
    private readonly ConcurrentQueue<IReadOnlyList<OutgoingMessage>> _calls = new();

    /// <summary>Messages in delivery order, without deduplicated repeats.</summary>
    public IReadOnlyList<OutgoingMessage> Delivered => _delivered.ToList();

    /// <summary>Every batch passed to <see cref="PublishAsync"/>, repeats included.</summary>
    public IReadOnlyList<IReadOnlyList<OutgoingMessage>> Calls => _calls.ToList();

    /// <summary>Returns an error for a message that should fail, or null to accept it.</summary>
    public Func<OutgoingMessage, string?> FailWith { get; set; } = _ => null;

    /// <summary>When set, the next call throws this before accepting anything.</summary>
    public Exception? ThrowNext { get; set; }

    /// <summary>When set, the next call accepts its messages and then throws this (a crash after publishing).</summary>
    public Exception? ThrowAfterAcceptingNext { get; set; }

    /// <summary>When set, a call signals <see cref="Entered"/> and waits for this before doing anything.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<Guid> DeliveredIds => Delivered.Select(m => m.EventId).ToList();

    public async Task<IReadOnlyList<PublishOutcome>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken ct)
    {
        _calls.Enqueue(batch);
        if (Gate is { } gate)
        {
            Gate = null;
            Entered.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        if (ThrowNext is { } thrown)
        {
            ThrowNext = null;
            throw thrown;
        }

        var outcomes = new List<PublishOutcome>();
        foreach (var message in batch)
        {
            var error = FailWith(message);
            if (error is null && _seen.TryAdd(message.EventId, true)) _delivered.Enqueue(message);
            outcomes.Add(new PublishOutcome(message.EventId, error));
        }
        if (ThrowAfterAcceptingNext is { } after)
        {
            ThrowAfterAcceptingNext = null;
            throw after;
        }
        return outcomes;
    }

}
