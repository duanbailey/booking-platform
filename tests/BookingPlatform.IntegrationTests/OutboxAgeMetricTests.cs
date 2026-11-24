using System.Text.Json;
using BookingPlatform.Api.Outbox;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T27a: the outbox-age metric behind the T27 alarm (decided 2026-11-24, plan/tasks.md): a CloudWatch Embedded Metric
/// Format line on stdout, at most once per Outbox:MetricIntervalSeconds, with the age in seconds of the oldest
/// unpublished row (0 when there is none), namespace BookingPlatform, no dimensions. Each test starts with an empty outbox.
/// </summary>
public class OutboxAgeMetricTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>, IAsyncLifetime
{
    private readonly StringWriter _output = new();

    public Task InitializeAsync() => fixture.Sql.ExecuteAsync("DELETE FROM outbox");

    public Task DisposeAsync() => Task.CompletedTask;

    private OutboxAgeMetric Metric(int intervalSeconds = 60) => new(
        fixture.Api.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Clock,
        Options.Create(new OutboxOptions { MetricIntervalSeconds = intervalSeconds }), _output);

    private Task RowAsync(DateTime createdAt, DateTime? publishedAt = null) => fixture.Sql.ExecuteAsync(
        "INSERT INTO outbox (id, event_type, schema_version, message_group_id, payload, created_at, published_at) " +
        "VALUES (@id, 'BookingCreated', 1, 'group-a', '{}', @createdAt, @publishedAt)",
        new { id = Guid.NewGuid(), createdAt, publishedAt });

    private List<JsonElement> Lines() => _output.ToString()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement)
        .ToList();

    [Fact]
    public async Task Writes_one_EMF_line_with_the_oldest_unpublished_rows_age()
    {
        await RowAsync(fixture.Now.AddSeconds(-90));
        await RowAsync(fixture.Now.AddSeconds(-30));
        await RowAsync(fixture.Now.AddSeconds(-600), publishedAt: fixture.Now.AddSeconds(-500)); // published: ignored

        Assert.True(await Metric().ReportIfDueAsync());

        var line = Assert.Single(Lines());
        Assert.Equal(90, line.GetProperty("OutboxOldestUnpublishedAgeSeconds").GetDouble());
        var aws = line.GetProperty("_aws");
        Assert.Equal(new DateTimeOffset(fixture.Now).ToUnixTimeMilliseconds(), aws.GetProperty("Timestamp").GetInt64());
        var directive = Assert.Single(aws.GetProperty("CloudWatchMetrics").EnumerateArray());
        Assert.Equal("BookingPlatform", directive.GetProperty("Namespace").GetString());
        Assert.Equal("[[]]", directive.GetProperty("Dimensions").GetRawText());
        var metric = Assert.Single(directive.GetProperty("Metrics").EnumerateArray());
        Assert.Equal("OutboxOldestUnpublishedAgeSeconds", metric.GetProperty("Name").GetString());
        Assert.Equal("Seconds", metric.GetProperty("Unit").GetString());
    }

    [Fact]
    public async Task Reports_zero_when_everything_is_published()
    {
        await RowAsync(fixture.Now.AddSeconds(-90), publishedAt: fixture.Now.AddSeconds(-89));

        await Metric().ReportIfDueAsync();

        Assert.Equal(0, Assert.Single(Lines()).GetProperty("OutboxOldestUnpublishedAgeSeconds").GetDouble());
    }

    [Fact]
    public async Task Writes_at_most_once_per_interval()
    {
        var metric = Metric(intervalSeconds: 60);

        Assert.True(await metric.ReportIfDueAsync());
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(await metric.ReportIfDueAsync());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await metric.ReportIfDueAsync());

        Assert.Equal(2, Lines().Count);
    }

    [Fact]
    public async Task A_database_failure_throws_and_writes_nothing_so_the_alarm_sees_missing_data()
    {
        await using var factory = new ApiFactory(fixture.Database, configure: builder =>
            builder.UseSetting("ConnectionStrings:BookingDb", "Server=127.0.0.1;Port=1;Database=none;User ID=nobody;Password=x;Connect Timeout=1;"));
        var metric = new OutboxAgeMetric(factory.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Clock,
            Options.Create(new OutboxOptions()), _output);

        await Assert.ThrowsAnyAsync<Exception>(() => metric.ReportIfDueAsync());

        Assert.Empty(_output.ToString());
    }
}
