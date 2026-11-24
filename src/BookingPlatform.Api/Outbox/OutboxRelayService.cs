using Amazon.SQS;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Outbox;

/// <summary>
/// Runs <see cref="OutboxRelay"/> in the API (ADR 0008). A round that published anything is followed at once by
/// another; otherwise the relay waits the poll interval (1 s by default). On shutdown it stops claiming and lets
/// the round in flight finish, so a sent batch is marked published (decided 2026-11-23). After each successful round it
/// reports the outbox-age metric (T27a).
/// </summary>
public sealed class OutboxRelayService(OutboxRelay relay, OutboxAgeMetric metric, IOptions<OutboxOptions> options, TimeProvider time, ILogger<OutboxRelayService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds);
        logger.LogInformation("Outbox relay started; publishing to {QueueUrl}", options.Value.QueueUrl);
        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await relay.RunRoundAsync(CancellationToken.None); // never cut short by shutdown
                await metric.ReportIfDueAsync(stoppingToken); // only after a successful round (T27a)
            }
            catch (Exception e)
            {
                logger.LogError(e, "An outbox relay round failed; trying again in {Interval}", interval);
            }
            if (published > 0) continue;

            try
            {
                await Task.Delay(interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>Logs once at startup that the relay is off (Development without Outbox:QueueUrl, decided 2026-11-23).</summary>
public sealed class OutboxRelayOffNotice(ILogger<OutboxRelayOffNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("Outbox:QueueUrl is not configured, so the outbox relay is off and events stay unpublished (allowed in Development only).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class OutboxRelayRegistration
{
    /// <summary>
    /// Registers the relay when Outbox:QueueUrl is set. Without it the relay is off in Development (one warning at
    /// startup) and startup fails anywhere else, so a deployment can't silently stop sending email (decided 2026-11-23).
    /// </summary>
    public static IServiceCollection AddOutboxRelay(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(OutboxOptions.Section);
        services.Configure<OutboxOptions>(section);
        if (string.IsNullOrWhiteSpace(section[nameof(OutboxOptions.QueueUrl)]))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"{OutboxOptions.Section}:{nameof(OutboxOptions.QueueUrl)} is not configured, so the outbox relay could not publish events. " +
                    "It may be left out only in Development.");
            }
            services.AddHostedService<OutboxRelayOffNotice>();
            return services;
        }

        services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(SqsConfig())); // default credential and region chain
        services.AddSingleton<IMessagePublisher, SqsMessagePublisher>();
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton(provider => new OutboxAgeMetric(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IOptions<OutboxOptions>>(), Console.Out));
        services.AddHostedService<OutboxRelayService>();
        return services;
    }

    /// <summary>
    /// SQS client settings (T21 review, 2026-11-23): 10 s per attempt and at most 2 retries, so a hung call ends
    /// within about half a minute as an ordinary failure (backoff, its group held back) instead of holding the
    /// relay's row locks and transaction for minutes. Region and credentials come from the default AWS chain.
    /// </summary>
    public static AmazonSQSConfig SqsConfig() => new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxErrorRetry = 2,
    };
}
