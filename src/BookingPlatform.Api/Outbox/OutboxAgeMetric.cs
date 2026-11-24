using System.Text.Json;
using BookingPlatform.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Outbox;

/// <summary>
/// The metric behind the outbox-age alarm (T27a, decided 2026-11-24): the age in seconds of the oldest unpublished
/// outbox row (0 when none), written as one CloudWatch Embedded Metric Format line straight to stdout (not through
/// the JSON logger, whose envelope CloudWatch can't read as EMF), at most once per Outbox:MetricIntervalSeconds.
/// Namespace BookingPlatform, metric OutboxOldestUnpublishedAgeSeconds, no dimensions. The relay calls it after each
/// successful round, so a relay that has stopped, or can't reach the database, writes nothing and the alarm's
/// missing-data rule fires; a group stuck on a failing row shows as a growing age.
/// </summary>
public sealed class OutboxAgeMetric(IServiceScopeFactory scopes, TimeProvider time, IOptions<OutboxOptions> options, TextWriter output)
{
    public const string Namespace = "BookingPlatform";
    public const string MetricName = "OutboxOldestUnpublishedAgeSeconds";

    private DateTimeOffset? _lastWritten;

    /// <summary>Writes the metric if the interval has passed since the last line; returns whether it wrote one.</summary>
    public async Task<bool> ReportIfDueAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (_lastWritten is { } last && now - last < TimeSpan.FromSeconds(options.Value.MetricIntervalSeconds)) return false;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var oldest = await db.Outbox.Where(m => m.PublishedAt == null).MinAsync(m => (DateTime?)m.CreatedAt, cancellationToken);
        var age = oldest is { } createdAt
            ? Math.Max(0, (now.UtcDateTime - DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)).TotalSeconds)
            : 0;

        var line = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["_aws"] = new
            {
                Timestamp = now.ToUnixTimeMilliseconds(),
                CloudWatchMetrics = new[]
                {
                    new { Namespace, Dimensions = new[] { Array.Empty<string>() }, Metrics = new[] { new { Name = MetricName, Unit = "Seconds" } } },
                },
            },
            [MetricName] = age,
        });
        await output.WriteLineAsync(line);
        await output.FlushAsync(cancellationToken);
        _lastWritten = now;
        return true;
    }
}
