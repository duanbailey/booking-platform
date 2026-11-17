using System.Net.Http.Headers;
using BookingPlatform.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API pipeline in memory against a <see cref="TestDatabase"/>, plus
/// the probe endpoints in <see cref="AccessProbeController"/> (test assembly only).
/// </summary>
public sealed class ApiFactory(TestDatabase database) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:BookingDb", database.ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-bytes");
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(AccessProbeController).Assembly));
    }

    public HttpClient ClientFor(Guid userId)
    {
        var client = CreateClient();
        var token = Services.GetRequiredService<AccessTokenIssuer>().Issue(userId).Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
