using BookingPlatform.Api.Outbox;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T21: the outbox relay, one round at a time against MySQL, with the in-memory queue (ADR 0007). Done when rows
/// are published in order per message group, at least once, and never lost between commit and publish.
/// Decisions 2026-11-23 (plan/tasks.md, T21 and "Outbox ordering"): each group in outbox.seq order; at most one row
/// per group per round, at most 10 rows; a row waiting for a retry, or with an earlier unpublished row in its group,
/// is not claimed; a failed row backs off 1 s doubling to 60 s and holds back only its group; nothing is skipped.
/// Each test starts with an empty outbox.
/// </summary>
public class OutboxRelayTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>, IAsyncLifetime
{
    private readonly InMemoryQueue _queue = new();
    private readonly CapturingLogger<OutboxRelay> _log = new();

    public Task InitializeAsync() => fixture.Sql.ExecuteAsync("DELETE FROM outbox");

    public Task DisposeAsync() => Task.CompletedTask;

    private OutboxRelay Relay(IMessagePublisher? publisher = null) =>
        new(fixture.Api.Services.GetRequiredService<IServiceScopeFactory>(), publisher ?? _queue, fixture.Clock, _log);

    /// <summary>Inserts an unpublished row; seq is assigned by the database in insert order.</summary>
    private async Task<Guid> RowAsync(string group, Guid? id = null, DateTime? createdAt = null)
    {
        var eventId = id ?? Guid.NewGuid();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO outbox (id, event_type, schema_version, message_group_id, payload, created_at) VALUES (@id, 'BookingCreated', 1, @group, @payload, @createdAt)",
            new { id = eventId, group, payload = $$"""{"eventId": "{{eventId}}", "group": "{{group}}"}""", createdAt = createdAt ?? fixture.Now });
        return eventId;
    }

    private async Task<Dictionary<string, object?>> StoredAsync(Guid id) =>
        (await fixture.Sql.RowAsync("SELECT * FROM outbox WHERE id = @id", new { id }))!;

    private async Task<DateTime?> PublishedAtAsync(Guid id) => (await StoredAsync(id))["published_at"] as DateTime?;

    /// <summary>Runs rounds until one publishes nothing (at most 20).</summary>
    private async Task DrainAsync(OutboxRelay relay)
    {
        for (var i = 0; i < 20 && await relay.RunRoundAsync() > 0; i++) { }
    }

    // ------------------------------------------------------------ Publishing

    [Fact]
    public async Task A_round_publishes_the_stored_envelope_with_its_group_and_eventId_and_marks_it_published()
    {
        var id = await RowAsync("group-a");
        var payload = Convert.ToString((await StoredAsync(id))["payload"])!;

        Assert.Equal(1, await Relay().RunRoundAsync());

        var message = Assert.Single(_queue.Delivered);
        Assert.Equal(id, message.EventId);
        Assert.Equal("group-a", message.MessageGroupId);
        Assert.Equal(payload, message.Body);
        var row = await StoredAsync(id);
        Assert.Equal(fixture.Now, row["published_at"]);
        Assert.Equal(0, Convert.ToInt32(row["attempts"]));
        Assert.Null(row["last_error"] as string);
    }

    [Fact]
    public async Task Published_rows_are_never_sent_again_and_an_empty_outbox_sends_nothing()
    {
        await RowAsync("group-a");
        var relay = Relay();
        await relay.RunRoundAsync();

        Assert.Equal(0, await relay.RunRoundAsync());
        Assert.Single(_queue.Calls);
    }

    [Fact]
    public async Task A_round_takes_at_most_ten_rows()
    {
        for (var i = 0; i < 12; i++) await RowAsync($"group-{i:D2}");
        var relay = Relay();

        Assert.Equal(10, await relay.RunRoundAsync());
        Assert.Equal(2, await relay.RunRoundAsync());
        Assert.Equal(12, _queue.Delivered.Count);
        Assert.All(_queue.Calls, call => Assert.True(call.Count <= 10));
    }

    // ------------------------------------------------------------ Order per message group

    [Fact]
    public async Task Each_group_is_published_in_seq_order_one_row_per_round()
    {
        var a1 = await RowAsync("group-a");
        var b1 = await RowAsync("group-b");
        var a2 = await RowAsync("group-a");
        var a3 = await RowAsync("group-a");
        var relay = Relay();

        Assert.Equal(2, await relay.RunRoundAsync());
        Assert.Equal([a1, b1], _queue.DeliveredIds);
        await DrainAsync(relay);

        Assert.Equal([a1, b1, a2, a3], _queue.DeliveredIds);
        Assert.All(_queue.Calls, call => Assert.Equal(call.Count, call.Select(m => m.MessageGroupId).Distinct().Count()));
    }

    [Fact]
    public async Task Order_follows_seq_not_created_at_or_id()
    {
        // created_at comes from each instance's clock and v7 ids are random within a millisecond (decided 2026-11-23).
        var first = await RowAsync("group-a", id: Guid.Parse("ffffffff-0000-7000-8000-000000000000"), createdAt: fixture.Now);
        var second = await RowAsync("group-a", id: Guid.Parse("00000000-0000-7000-8000-000000000000"), createdAt: fixture.Now.AddSeconds(-5));

        await DrainAsync(Relay());

        Assert.Equal([first, second], _queue.DeliveredIds);
    }

    // ------------------------------------------------------------ Failures and retries

    [Fact]
    public async Task A_failed_row_is_kept_with_its_error_and_holds_back_only_its_group()
    {
        var failing = await RowAsync("group-a");
        var later = await RowAsync("group-a");
        var other = await RowAsync("group-b");
        _queue.FailWith = m => m.EventId == failing ? "InvalidMessageContents: too large" : null;
        var relay = Relay();

        await DrainAsync(relay);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5)); // long past any backoff: still blocked while it keeps failing
        try
        {
            await DrainAsync(relay);

            Assert.Equal([other], _queue.DeliveredIds);
            var row = await StoredAsync(failing);
            Assert.Null(row["published_at"] as DateTime?);
            Assert.Equal(2, Convert.ToInt32(row["attempts"]));
            Assert.Equal("InvalidMessageContents: too large", row["last_error"]);
            Assert.Null(await PublishedAtAsync(later));

            // Once it goes through, the group continues in order.
            _queue.FailWith = _ => null;
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await DrainAsync(relay);
            Assert.Equal([other, failing, later], _queue.DeliveredIds);
            Assert.Null((await StoredAsync(failing))["last_error"] as string);
        }
        finally
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(-6));
        }
    }

    [Fact]
    public async Task A_failed_row_backs_off_from_one_second_doubling_up_to_sixty()
    {
        var id = await RowAsync("group-a");
        _queue.FailWith = _ => "ServiceUnavailable";
        var relay = Relay();
        var start = fixture.Now;
        var delays = new List<double>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                Assert.Equal(0, await relay.RunRoundAsync());
                var next = (DateTime)(await StoredAsync(id))["next_attempt_at"]!;
                delays.Add((next - fixture.Now).TotalSeconds);

                // Not tried again before next_attempt_at, then tried exactly at it.
                fixture.Clock.Advance(next - fixture.Now - TimeSpan.FromTicks(10));
                var calls = _queue.Calls.Count;
                await relay.RunRoundAsync();
                Assert.Equal(calls, _queue.Calls.Count);
                fixture.Clock.Advance(TimeSpan.FromTicks(10));
            }
        }
        finally
        {
            fixture.Clock.Advance(start - fixture.Now);
        }

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], delays);
        Assert.Equal(8, Convert.ToInt32((await StoredAsync(id))["attempts"]));
    }

    [Fact]
    public async Task A_call_that_throws_fails_every_row_in_it_and_the_error_is_logged_without_the_payload()
    {
        var a = await RowAsync("group-a");
        var b = await RowAsync("group-b");
        _queue.ThrowNext = new HttpRequestException("connection refused");

        Assert.Equal(0, await Relay().RunRoundAsync());

        foreach (var id in new[] { a, b })
        {
            var row = await StoredAsync(id);
            Assert.Null(row["published_at"] as DateTime?);
            Assert.Equal(1, Convert.ToInt32(row["attempts"]));
            Assert.Contains("connection refused", Convert.ToString(row["last_error"]));
        }
        var errors = _log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, m => m.Contains(a.ToString()) && m.Contains("group-a") && m.Contains("attempt 1"));
        Assert.DoesNotContain(errors, m => m.Contains("\"group\""));
    }

    [Fact]
    public async Task A_long_error_is_truncated_to_the_column()
    {
        var id = await RowAsync("group-a");
        _queue.FailWith = _ => new string('x', 5000);

        await Relay().RunRoundAsync();

        Assert.Equal(1000, Convert.ToString((await StoredAsync(id))["last_error"])!.Length);
    }

    // ------------------------------------------------------------ At least once, never lost

    [Fact]
    public async Task A_crash_after_publishing_republishes_with_the_same_deduplication_ID()
    {
        var id = await RowAsync("group-a");
        _queue.ThrowAfterAcceptingNext = new TaskCanceledException("lost the response");
        var relay = Relay();

        await relay.RunRoundAsync();
        Assert.Null(await PublishedAtAsync(id));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        try
        {
            await relay.RunRoundAsync();
        }
        finally
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(-1));
        }

        // Sent twice with the same eventId, delivered once (SQS deduplication, ADR 0008).
        Assert.Equal(2, _queue.Calls.Count(c => c.Any(m => m.EventId == id)));
        Assert.Equal([id], _queue.DeliveredIds);
        Assert.NotNull(await PublishedAtAsync(id));
    }

    [Fact]
    public async Task An_uncommitted_event_is_neither_published_nor_waited_for_and_a_rolled_back_one_never_is()
    {
        var committed = await RowAsync("group-b");
        var uncommitted = Guid.NewGuid();
        var relay = Relay();

        await using (var open = await fixture.Sql.BeginTransactionAsync())
        {
            await open.ExecuteAsync(
                "INSERT INTO outbox (id, event_type, schema_version, message_group_id, payload, created_at) VALUES (@id, 'BookingCreated', 1, 'group-a', '{}', @now)",
                new { id = uncommitted, now = fixture.Now });

            var round = relay.RunRoundAsync();
            Assert.Same(round, await Task.WhenAny(round, Task.Delay(TimeSpan.FromSeconds(5)))); // not blocked by the open insert
            Assert.Equal(1, await round);
        } // rolled back

        await DrainAsync(relay);
        Assert.Equal([committed], _queue.DeliveredIds);
    }

    // ------------------------------------------------------------ Several relays (ADR 0008)

    [Fact]
    public async Task Two_relays_never_send_the_same_row_and_never_overtake_a_claimed_row_in_its_group()
    {
        var a1 = await RowAsync("group-a");
        var a2 = await RowAsync("group-a");
        var b1 = await RowAsync("group-b");
        var gate = new TaskCompletionSource();
        var slowQueue = new InMemoryQueue { FailWith = m => m.EventId == b1 ? "make b1 wait" : null, Gate = gate };
        var slow = Relay(slowQueue);
        var fast = Relay();

        // The slow relay claims a1 and b1 and stalls while sending, holding their row locks.
        var slowRound = slow.RunRoundAsync();
        await slowQueue.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The fast relay skips the claimed rows, and must not send a2 while a1 is unpublished.
        Assert.Equal(0, await fast.RunRoundAsync());
        Assert.Empty(_queue.Calls);

        // Released: the slow relay publishes a1; b1 fails there and backs off.
        gate.SetResult();
        Assert.Equal(1, await slowRound);
        Assert.Equal([a1], slowQueue.DeliveredIds);

        Assert.Equal(1, await fast.RunRoundAsync());
        Assert.Equal([a2], _queue.DeliveredIds);
    }
}
