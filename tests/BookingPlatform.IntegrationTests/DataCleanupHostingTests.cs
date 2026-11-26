using BookingPlatform.Api.Maintenance;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T31: the cleanup as a background service in the API. Decisions 2026-11-26 (plan/tasks.md, T31): it runs
/// whether or not the outbox relay is on, once at startup and then every hour. Test hosts turn it off by default
/// (ApiFactory), so it can't delete rows other tests set up; these tests turn it back on. Each test starts its own
/// API host on this class's database.
/// </summary>
public class DataCleanupHostingTests : IAsyncLifetime
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

    private ApiFactory Host(bool? enabled) =>
        new(_database, configure: builder =>
        {
            builder.UseEnvironment("Development"); // no Outbox:QueueUrl, so the relay is off
            if (enabled is not null) builder.UseSetting("Cleanup:Enabled", enabled.Value.ToString());
        });

    private async Task<Guid> RowAsync(int publishedDaysAgo)
    {
        var id = Guid.NewGuid();
        await _sql.ExecuteAsync(
            "INSERT INTO outbox (id, event_type, schema_version, message_group_id, payload, created_at, published_at) " +
            "VALUES (@id, 'BookingCreated', 1, 'group-a', '{}', UTC_TIMESTAMP(6) - INTERVAL @days DAY, UTC_TIMESTAMP(6) - INTERVAL @days DAY)",
            new { id, days = publishedDaysAgo });
        return id;
    }

    private async Task<long> CountAsync(Guid id) =>
        Convert.ToInt64(await _sql.ScalarAsync("SELECT COUNT(*) FROM outbox WHERE id = @id", new { id }));

    [Fact]
    public async Task The_running_API_cleans_up_at_startup_even_with_the_relay_off()
    {
        var old = await RowAsync(publishedDaysAgo: 31);
        var recent = await RowAsync(publishedDaysAgo: 1);
        await using var host = Host(enabled: true);
        host.CreateClient(); // starts the host

        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is DataCleanupService);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await CountAsync(old) > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(0, await CountAsync(old));
        Assert.Equal(1, await CountAsync(recent));
    }

    [Theory]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "-1")]
    [InlineData("BatchSize", "10001")]
    [InlineData("IntervalMinutes", "0")]
    [InlineData("IntervalMinutes", "-5")]
    [InlineData("IntervalMinutes", "1441")]
    public async Task Startup_fails_on_a_setting_out_of_range(string setting, string value)
    {
        // T31 review (2026-11-26): a batch size of 0 would loop forever, a negative one fail every run, and an
        // interval of 0 or less spin or stop the service; so they are refused at startup.
        await using var host = new ApiFactory(_database, configure: builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Cleanup:Enabled", "true");
            builder.UseSetting($"Cleanup:{setting}", value);
        });

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains(setting, error.ToString());
    }

    [Fact]
    public async Task The_largest_settings_in_range_start()
    {
        await using var host = new ApiFactory(_database, configure: builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Cleanup:Enabled", "true");
            builder.UseSetting("Cleanup:BatchSize", "10000");
            builder.UseSetting("Cleanup:IntervalMinutes", "1440");
        });

        host.CreateClient();
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is DataCleanupService);
    }

    [Fact]
    public async Task A_failed_run_is_logged_as_an_error_and_the_service_keeps_running()
    {
        var log = new CapturingLogger<DataCleanupService>();
        var options = Options.Create(new CleanupOptions());
        var cleanup = new DataCleanup(new FailingScopes(), TimeProvider.System, options, new CapturingLogger<DataCleanup>());
        using var service = new DataCleanupService(cleanup, options, log);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (log.Entries.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("A cleanup run failed; trying again in 01:00:00", entry.Message);
        Assert.False(service.ExecuteTask!.IsCompleted); // waiting for the next run, not stopped

        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class FailingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("The database is down.");
    }

    [Fact]
    public async Task A_CLI_command_never_runs_the_cleanup()
    {
        // The CLI commands don't register it (Program.cs), and they never start the host's services either, so
        // this guards the behaviour rather than the registration: an old published row survives an admin command.
        var old = await RowAsync(publishedDaysAgo: 31);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var apiDll = AdminProcessTests.FindApiDll();
        start.WorkingDirectory = Path.GetDirectoryName(apiDll)!;
        start.ArgumentList.Add(apiDll);
        start.ArgumentList.Add("admin");
        start.ArgumentList.Add("list");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__BookingDb"] = _database.ConnectionString;

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        await Task.Delay(500); // a cleanup started by the command would have run by now

        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.Equal(1, await CountAsync(old));
    }

    [Fact]
    public async Task It_is_on_unless_configured_off()
    {
        Assert.True(new CleanupOptions().Enabled); // a deployment configures nothing, so it is on

        await using var off = Host(enabled: false);
        off.CreateClient();
        Assert.DoesNotContain(off.Services.GetServices<IHostedService>(), s => s is DataCleanupService);
    }
}
