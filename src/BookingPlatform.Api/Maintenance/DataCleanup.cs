using System.ComponentModel.DataAnnotations;
using System.Data;
using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Maintenance;

/// <summary>The cleanup job's settings (section "Cleanup"). Decided 2026-11-26 (plan/tasks.md, T31).</summary>
public sealed class CleanupOptions
{
    public const string Section = "Cleanup";

    /// <summary>On unless configured off; test hosts turn it off so it can't delete rows a test set up.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The time between runs; the first run is at startup. Checked at startup, at most a day.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Rows deleted per statement (and transaction). Checked at startup: 0 would loop forever.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 1000;
}

public sealed record CleanupResult(int OutboxRowsDeleted, int IdempotencyKeysDeleted);

/// <summary>
/// T31: keeps the outbox and idempotency_keys bounded. Decisions 2026-11-26 (plan/tasks.md, T31):
/// <list type="bullet">
/// <item>Outbox rows published more than <see cref="OutboxRetention"/> ago (strictly older) are deleted; unpublished
/// rows never are. Until then a published row helps trace a missing email (T21).</item>
/// <item>idempotency_keys rows are deleted once expired: created_at at or before now - 24 h (T29).</item>
/// <item>Deletes run a batch at a time, each its own short transaction at READ COMMITTED, so no gap locks hold up
/// the API's inserts (new rows land at the end of both indexes), back to back until a batch comes back short.
/// Several API instances running it at once is harmless: a row is deleted once, and the others skip it.</item>
/// </list>
/// The (published_at, seq) index serves the outbox delete, the created_at index the idempotency_keys one.
/// </summary>
public sealed class DataCleanup(IServiceScopeFactory scopes, TimeProvider time, IOptions<CleanupOptions> options, ILogger<DataCleanup> logger)
{
    public static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(30);

    /// <summary>Runs once and returns how many rows it deleted from each table.</summary>
    public async Task<CleanupResult> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var batch = options.Value.BatchSize;
        var outboxCutoff = now - OutboxRetention;
        var keyCutoff = now - IdempotencyKeys.Lifetime;

        var outbox = await DeleteInBatchesAsync(db => db.Database.ExecuteSqlAsync($"""
            DELETE FROM outbox WHERE published_at < {outboxCutoff} ORDER BY published_at LIMIT {batch}
            """, ct), batch, ct);
        var keys = await DeleteInBatchesAsync(db => db.Database.ExecuteSqlAsync($"""
            DELETE FROM idempotency_keys WHERE created_at <= {keyCutoff} ORDER BY created_at LIMIT {batch}
            """, ct), batch, ct);

        var result = new CleanupResult(outbox, keys);
        if (outbox > 0 || keys > 0)
        {
            logger.LogInformation("Cleanup deleted {OutboxRowsDeleted} outbox rows published before {OutboxCutoff:O} and {IdempotencyKeysDeleted} idempotency keys created at or before {KeyCutoff:O}",
                outbox, outboxCutoff, keys, keyCutoff);
        }
        return result;
    }

    private async Task<int> DeleteInBatchesAsync(Func<BookingDbContext, Task<int>> deleteBatch, int batchSize, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var deleted = await ReadCommittedAsync(scopes, deleteBatch, ct);
            total += deleted;
            if (deleted < batchSize) return total;
        }
    }

    /// <summary>
    /// Deletes one caller's key if it has expired, in its own READ COMMITTED transaction on its own connection,
    /// for the booking endpoint (T29) to call before inserting a new row for the key. The cleanup may delete the
    /// same row at any moment; at READ COMMITTED a delete that finds no row takes no gap lock, whereas inside the
    /// booking's REPEATABLE READ transaction it would, and could deadlock other callers' inserts (T31 review,
    /// 2026-11-26). Nothing references the row, so deleting it early is safe whether or not the booking commits.
    /// </summary>
    public static Task DeleteExpiredKeyAsync(IServiceScopeFactory scopes, Guid userId, string key, DateTime now, CancellationToken ct)
    {
        var cutoff = now - IdempotencyKeys.Lifetime;
        return ReadCommittedAsync(scopes, db => db.Database.ExecuteSqlAsync(
            $"DELETE FROM idempotency_keys WHERE user_id = {userId} AND idempotency_key = {key} AND created_at <= {cutoff}", ct), ct);
    }

    private static async Task<int> ReadCommittedAsync(IServiceScopeFactory scopes, Func<BookingDbContext, Task<int>> statement, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var affected = await statement(db);
        await transaction.CommitAsync(ct);
        return affected;
    }
}

/// <summary>
/// Runs <see cref="DataCleanup"/> in the API (T31): once at startup, then every <see cref="CleanupOptions.IntervalMinutes"/>.
/// A failed run is logged as an error and tried again at the next run.
/// </summary>
public sealed class DataCleanupService(DataCleanup cleanup, IOptions<CleanupOptions> options, ILogger<DataCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.IntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cleanup.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "A cleanup run failed; trying again in {Interval}", interval);
            }

            try
            {
                await Task.Delay(interval, stoppingToken); // the real clock: hours, not test time
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

public static class DataCleanupRegistration
{
    /// <summary>
    /// Registers the cleanup unless Cleanup:Enabled is false. It runs whether or not the outbox relay is on.
    /// </summary>
    public static IServiceCollection AddDataCleanup(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CleanupOptions.Section);
        if (!section.GetValue(nameof(CleanupOptions.Enabled), true))
        {
            services.Configure<CleanupOptions>(section);
            return services;
        }

        // Out-of-range settings stop startup (T31 review, 2026-11-26), as a missing queue URL does for the relay.
        services.AddOptions<CleanupOptions>().Bind(section).ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton<DataCleanup>();
        services.AddHostedService<DataCleanupService>();
        return services;
    }
}
