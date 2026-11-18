using System.Net;
using System.Net.Http.Headers;
using BookingPlatform.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API pipeline in memory against a <see cref="TestDatabase"/>, plus
/// the probe endpoints in <see cref="AccessProbeController"/> (test assembly only).
/// The /v1/auth/ rate limits are high unless a test asks for low ones. With a
/// <paramref name="time"/>, the API reads the clock from it instead of the system clock.
/// </summary>
public sealed class ApiFactory(
    TestDatabase database, int authPermitLimit = 10_000, int refreshPermitLimit = 10_000, TimeProvider? time = null)
    : WebApplicationFactory<Program>
{
    /// <summary>Test-only request header that sets the caller's IP (the test server has none).</summary>
    public const string ClientIpHeader = "X-Test-Client-Ip";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:BookingDb", database.ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-bytes");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", authPermitLimit.ToString());
        builder.UseSetting("RateLimiting:Refresh:PermitLimit", refreshPermitLimit.ToString());
        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(AccessProbeController).Assembly);
            services.AddTransient<IStartupFilter, ClientIpStartupFilter>();
            if (time is not null) services.AddSingleton(time); // the last registration wins
        });
    }

    public HttpClient ClientFor(Guid userId)
    {
        var client = CreateClient();
        var token = Services.GetRequiredService<AccessTokenIssuer>().Issue(userId).Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient ClientFrom(string ip)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ClientIpHeader, ip);
        return client;
    }

    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[ClientIpHeader], out var ip))
                {
                    context.Connection.RemoteIpAddress = ip;
                }
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
