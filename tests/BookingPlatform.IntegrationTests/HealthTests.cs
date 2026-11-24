using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T27a: GET /health for the load balancer's health check (specs/04-api.openapi.yaml, decided 2026-11-24): anonymous,
/// outside /v1, and shallow, so a database outage doesn't make the load balancer replace every task.
/// </summary>
public class HealthTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Health_is_anonymous_and_says_ok()
    {
        using var client = fixture.Api.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Single(body.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task Health_does_not_touch_the_database()
    {
        await using var factory = new ApiFactory(fixture.Database, configure: builder =>
            builder.UseSetting("ConnectionStrings:BookingDb", "Server=127.0.0.1;Port=1;Database=none;User ID=nobody;Password=x;Connect Timeout=1;"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_not_under_v1()
    {
        using var client = fixture.Api.CreateClient();

        using var response = await client.GetAsync("/v1/health");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
