using BookingPlatform.Api.Maintenance;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T31: the cleanup job, one run at a time against MySQL. Done when old published outbox rows are deleted and
/// unpublished or recent ones never are. Decisions 2026-11-26 (plan/tasks.md, T31): outbox rows published more
/// than 30 days ago (strictly older) are deleted; idempotency_keys rows once expired (created_at at or before
/// now - 24 h, T29); in batches, back to back until a batch comes back short; an info line with the counts only
/// when something was deleted. Each test starts with an empty outbox and no idempotency keys.
/// </summary>
public class DataCleanupTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>, IAsyncLifetime
{
    private readonly CapturingLogger<DataCleanup> _log = new();

    public async Task InitializeAsync()
    {
        await fixture.Sql.ExecuteAsync("DELETE FROM outbox");
        await fixture.Sql.ExecuteAsync("DELETE FROM idempotency_keys");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private DataCleanup Cleanup(int batchSize = 1000) =>
        new(fixture.Api.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Clock,
            Options.Create(new CleanupOptions { BatchSize = batchSize }), _log);

    private async Task<Guid> OutboxRowAsync(DateTime? publishedAt, DateTime? createdAt = null, int attempts = 0)
    {
        var id = Guid.NewGuid();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO outbox (id, event_type, schema_version, message_group_id, payload, created_at, published_at, attempts) " +
            "VALUES (@id, 'BookingCreated', 1, @group, '{}', @createdAt, @publishedAt, @attempts)",
            new { id, group = $"group-{id:N}", createdAt = createdAt ?? publishedAt ?? fixture.Now, publishedAt, attempts });
        return id;
    }

    private async Task<string> IdempotencyKeyAsync(DateTime createdAt)
    {
        var key = Guid.NewGuid().ToString("N");
        var booking = await fixture.Sql.InsertBookingAsync(await fixture.Sql.InsertBookingFixtureAsync());
        var user = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO idempotency_keys (user_id, idempotency_key, request_hash, booking_id, created_at) " +
            "VALUES (@user, @key, @hash, @booking, @createdAt)",
            new { user, key, hash = new string('a', 64), booking, createdAt });
        return key;
    }

    private async Task<List<Guid>> OutboxIdsAsync() =>
        (await fixture.Sql.RowsAsync("SELECT id FROM outbox")).Select(r => Guid.Parse(Convert.ToString(r["id"])!)).ToList();

    private async Task<List<string>> IdempotencyKeysAsync() =>
        (await fixture.Sql.RowsAsync("SELECT idempotency_key FROM idempotency_keys")).Select(r => Convert.ToString(r["idempotency_key"])!).ToList();

    private static readonly TimeSpan Microsecond = TimeSpan.FromTicks(10);

    // ------------------------------------------------------------ Outbox

    [Fact]
    public async Task Rows_published_more_than_30_days_ago_are_deleted_and_recent_ones_are_kept()
    {
        var old = await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-30) - Microsecond);
        var veryOld = await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-400));
        var exactly30Days = await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-30));
        var recent = await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-1));
        // Created long ago but published recently: the age counts from publishing.
        var publishedLate = await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-2), createdAt: fixture.Now.AddDays(-60));

        var result = await Cleanup().RunAsync();

        Assert.Equal(2, result.OutboxRowsDeleted);
        Assert.Equivalent(new[] { exactly30Days, recent, publishedLate }, await OutboxIdsAsync(), strict: true);
        Assert.DoesNotContain(old, await OutboxIdsAsync());
        Assert.DoesNotContain(veryOld, await OutboxIdsAsync());
    }

    [Fact]
    public async Task Unpublished_rows_are_never_deleted_however_old()
    {
        var waiting = await OutboxRowAsync(publishedAt: null, createdAt: fixture.Now.AddDays(-400));
        var failing = await OutboxRowAsync(publishedAt: null, createdAt: fixture.Now.AddDays(-90), attempts: 5000);

        var result = await Cleanup().RunAsync();

        Assert.Equal(0, result.OutboxRowsDeleted);
        Assert.Equivalent(new[] { waiting, failing }, await OutboxIdsAsync(), strict: true);
    }

    // ------------------------------------------------------------ Idempotency keys

    [Fact]
    public async Task Expired_idempotency_keys_are_deleted_and_live_ones_are_kept()
    {
        var exactly24Hours = await IdempotencyKeyAsync(fixture.Now.AddHours(-24)); // expired at exactly 24 h (T29)
        var old = await IdempotencyKeyAsync(fixture.Now.AddDays(-3));
        var live = await IdempotencyKeyAsync(fixture.Now.AddHours(-24) + Microsecond);
        var fresh = await IdempotencyKeyAsync(fixture.Now);
        var bookings = Convert.ToInt32(await fixture.Sql.ScalarAsync("SELECT COUNT(*) FROM bookings"));

        var result = await Cleanup().RunAsync();

        Assert.Equal(2, result.IdempotencyKeysDeleted);
        Assert.Equivalent(new[] { live, fresh }, await IdempotencyKeysAsync(), strict: true);
        Assert.DoesNotContain(exactly24Hours, await IdempotencyKeysAsync());
        Assert.DoesNotContain(old, await IdempotencyKeysAsync());
        Assert.Equal(bookings, Convert.ToInt32(await fixture.Sql.ScalarAsync("SELECT COUNT(*) FROM bookings"))); // only the key rows go
    }

    // ------------------------------------------------------------ Batches

    [Theory]
    [InlineData(7)] // two full batches and a short one
    [InlineData(6)] // two full batches, then an empty one
    public async Task A_run_deletes_in_batches_until_nothing_old_is_left(int count)
    {
        for (var i = 0; i < count; i++) await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-31 - i));
        for (var i = 0; i < count; i++) await IdempotencyKeyAsync(fixture.Now.AddDays(-2 - i));
        var keep = await OutboxRowAsync(publishedAt: fixture.Now);

        var result = await Cleanup(batchSize: 3).RunAsync();

        Assert.Equal(new CleanupResult(count, count), result);
        Assert.Equal([keep], await OutboxIdsAsync());
        Assert.Empty(await IdempotencyKeysAsync());
    }

    // ------------------------------------------------------------ Logging

    [Fact]
    public async Task A_run_that_deleted_something_logs_the_counts_and_one_that_deleted_nothing_logs_nothing()
    {
        await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-31));
        await OutboxRowAsync(publishedAt: fixture.Now.AddDays(-32));
        await IdempotencyKeyAsync(fixture.Now.AddDays(-2));
        var cleanup = Cleanup();

        await cleanup.RunAsync();
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("2 outbox rows", entry.Message);
        Assert.Contains("1 idempotency keys", entry.Message);

        Assert.Equal(new CleanupResult(0, 0), await cleanup.RunAsync());
        Assert.Single(_log.Entries);
    }
}
