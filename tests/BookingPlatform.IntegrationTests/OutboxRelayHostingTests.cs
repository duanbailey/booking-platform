using BookingPlatform.Api.Outbox;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T21: the relay as a background service in the API (ADR 0008). Decisions 2026-11-23 (plan/tasks.md, T21):
/// without Outbox:QueueUrl the relay is off in Development and startup fails elsewhere; a round that published
/// anything is followed at once by another; on shutdown the round in flight finishes. Each test starts its own
/// API host, with the in-memory queue in place of SQS, on this class's database.
/// </summary>
public class OutboxRelayHostingTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();
    private Sql _sql = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _sql = new Sql(_database.ConnectionString);
        await _sql.ExecuteAsync("DELETE FROM outbox");
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    private ApiFactory Host(string environment, string? queueUrl, InMemoryQueue? queue = null, int pollMilliseconds = 1000) =>
        new(_database, configure: builder =>
        {
            builder.UseEnvironment(environment);
            if (queueUrl is not null) builder.UseSetting("Outbox:QueueUrl", queueUrl);
            builder.UseSetting("Outbox:PollIntervalMilliseconds", pollMilliseconds.ToString());
            if (queue is not null) builder.ConfigureServices(services => services.AddSingleton<IMessagePublisher>(queue));
        });

    private async Task<Guid> RowAsync(string group)
    {
        var id = Guid.NewGuid();
        await _sql.ExecuteAsync(
            "INSERT INTO outbox (id, event_type, schema_version, message_group_id, payload, created_at) VALUES (@id, 'BookingCreated', 1, @group, '{}', UTC_TIMESTAMP(6))",
            new { id, group });
        return id;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(condition(), $"Not true within {timeout}.");
    }

    [Fact]
    public async Task In_Development_without_a_queue_the_relay_is_off()
    {
        await RowAsync("group-a");
        await using var host = Host("Development", queueUrl: null);
        host.CreateClient(); // starts the host

        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), s => s is OutboxRelayService);
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is OutboxRelayOffNotice);
        Assert.Null(host.Services.GetService<OutboxRelay>());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Anywhere_else_startup_fails_without_a_queue(string environment)
    {
        await using var host = Host(environment, queueUrl: null);

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("Outbox:QueueUrl is not configured", error.ToString());
    }

    [Fact]
    public async Task With_a_queue_the_running_API_publishes_committed_rows()
    {
        var queue = new InMemoryQueue();
        await using var host = Host("Production", "https://sqs.eu-west-1.amazonaws.com/000000000000/test.fifo", queue);
        host.CreateClient();

        var id = await RowAsync("group-a"); // committed while the API runs
        await WaitUntilAsync(() => queue.DeliveredIds.Contains(id), TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _sql.ScalarAsync("SELECT published_at FROM outbox WHERE id = @id", new { id }).Result is DateTime, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_round_that_published_something_is_followed_at_once()
    {
        // Three rows of one group need three rounds; with a 60 s poll interval only back-to-back rounds finish quickly.
        var ids = new[] { await RowAsync("group-a"), await RowAsync("group-a"), await RowAsync("group-a") };
        var queue = new InMemoryQueue();
        await using var host = Host("Development", "https://sqs.eu-west-1.amazonaws.com/000000000000/test.fifo", queue, pollMilliseconds: 60_000);
        host.CreateClient();

        await WaitUntilAsync(() => queue.Delivered.Count == 3, TimeSpan.FromSeconds(10));
        Assert.Equal(ids, queue.DeliveredIds);
    }

    [Fact]
    public async Task On_shutdown_the_round_in_flight_finishes_and_marks_its_rows_published()
    {
        var id = await RowAsync("group-a");
        var gate = new TaskCompletionSource();
        var queue = new InMemoryQueue { Gate = gate };
        var host = Host("Development", "https://sqs.eu-west-1.amazonaws.com/000000000000/test.fifo", queue);
        host.CreateClient();
        await queue.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopping = host.DisposeAsync().AsTask();
        await Task.Delay(200);
        gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([id], queue.DeliveredIds);
        Assert.IsType<DateTime>(await _sql.ScalarAsync("SELECT published_at FROM outbox WHERE id = @id", new { id }));
    }
}
