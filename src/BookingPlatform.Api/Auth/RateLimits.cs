using System.Globalization;
using System.Threading.RateLimiting;
using BookingPlatform.Api.Errors;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Auth;

/// <summary>One fixed-window rate limit, bound from "RateLimiting:Auth" or "RateLimiting:Refresh".</summary>
public sealed class RateLimitOptions
{
    public int PermitLimit { get; set; }
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Rate limiting (specs/07-nfr.md), a fixed window per client IP for each policy: Auth
/// (10 a minute) for the /v1/auth/ endpoints that check a password or send email, Refresh
/// (60 a minute) for refresh and logout. Over the limit the answer is 429 RequestRejected
/// with Retry-After.
/// </summary>
public static class RateLimits
{
    public const string Auth = "auth";
    public const string Refresh = "refresh";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>(Auth).Bind(configuration.GetSection("RateLimiting:Auth"))
            .Configure(o => o.PermitLimit = o.PermitLimit > 0 ? o.PermitLimit : 10);
        services.AddOptions<RateLimitOptions>(Refresh).Bind(configuration.GetSection("RateLimiting:Refresh"))
            .Configure(o => o.PermitLimit = o.PermitLimit > 0 ? o.PermitLimit : 60);

        return services.AddRateLimiter(options =>
        {
            options.AddPolicy(Auth, context => PerClientIp(context, Auth));
            options.AddPolicy(Refresh, context => PerClientIp(context, Refresh));

            options.OnRejected = (context, _) =>
            {
                var response = context.HttpContext.Response;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                    response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                }
                return new ValueTask(ErrorResponses.WriteAsync(
                    context.HttpContext, ApiException.RequestRejected(StatusCodes.Status429TooManyRequests)));
            };
        });
    }

    private static RateLimitPartition<string> PerClientIp(HttpContext context, string policy)
    {
        var settings = context.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitOptions>>().Get(policy);
        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PermitLimit, Window = settings.Window, QueueLimit = 0 });
    }
}
