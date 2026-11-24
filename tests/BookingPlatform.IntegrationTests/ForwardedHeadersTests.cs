using System.Net;
using System.Net.Http.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T27a: trusted proxies and the HTTPS redirect behind CloudFront and the internal load balancer (decided 2026-11-24,
/// plan/tasks.md). ForwardedHeaders:KnownNetworks and ForwardedHeaders:ForwardLimit say which hops to trust, so rate
/// limiting (specs/07-nfr.md) sees the real client and a client can't spoof its IP; the defaults keep trusting only
/// loopback. Https:Redirect turns the API's own redirect off where CloudFront enforces HTTPS. The test server's
/// client IP is the load balancer's (ApiFactory.ClientIpHeader).
/// </summary>
public class ForwardedHeadersTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ApiFactory Factory(params (string Key, string Value)[] settings) =>
        new(fixture.Database, configure: builder =>
        {
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });

    /// <summary>The CloudFront + load balancer chain: trust the VPC, two hops.</summary>
    private ApiFactory BehindCloudFront(params (string Key, string Value)[] extra) =>
        Factory([("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/16"), ("ForwardedHeaders:ForwardLimit", "2"), .. extra]);

    private static async Task<ClientProbeController.Probe> ProbeAsync(ApiFactory factory, string peer, string? forwardedFor, string? proto = null)
    {
        using var client = factory.ClientFrom(peer);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/client");
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (proto is not null) request.Headers.Add("X-Forwarded-Proto", proto);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClientProbeController.Probe>())!;
    }

    [Fact]
    public async Task By_default_only_loopback_is_trusted_so_a_forwarded_header_from_elsewhere_is_ignored()
    {
        await using var factory = Factory();

        var probe = await ProbeAsync(factory, peer: "10.0.1.5", forwardedFor: "203.0.113.7");

        Assert.Equal("10.0.1.5", probe.Ip);
    }

    [Fact]
    public async Task Behind_CloudFront_and_the_load_balancer_the_client_is_the_entry_before_the_trusted_hop()
    {
        await using var factory = BehindCloudFront();

        var probe = await ProbeAsync(factory, peer: "10.0.1.5", forwardedFor: "203.0.113.7, 10.0.2.9", proto: "https");

        Assert.Equal("203.0.113.7", probe.Ip);
        Assert.Equal("https", probe.Scheme);
    }

    [Fact]
    public async Task A_client_cannot_spoof_its_IP_by_sending_its_own_forwarded_header()
    {
        await using var factory = BehindCloudFront();

        // The client sent "X-Forwarded-For: 198.51.100.1"; CloudFront appended the real address, the VPC hop followed.
        var probe = await ProbeAsync(factory, peer: "10.0.1.5", forwardedFor: "198.51.100.1, 203.0.113.7, 10.0.2.9");

        Assert.Equal("203.0.113.7", probe.Ip);
    }

    [Fact]
    public async Task An_untrusted_hop_ends_the_chain()
    {
        await using var factory = BehindCloudFront();

        var probe = await ProbeAsync(factory, peer: "10.0.1.5", forwardedFor: "198.51.100.1, 203.0.113.50");

        Assert.Equal("203.0.113.50", probe.Ip); // 203.0.113.50 isn't a trusted proxy, so what it claims is ignored
    }

    [Fact]
    public async Task A_peer_outside_the_trusted_network_is_the_client_whatever_it_forwards()
    {
        await using var factory = BehindCloudFront();

        var probe = await ProbeAsync(factory, peer: "198.51.100.9", forwardedFor: "203.0.113.7", proto: "https");

        Assert.Equal("198.51.100.9", probe.Ip);
        Assert.Equal("http", probe.Scheme);
    }

    [Fact]
    public void A_malformed_trusted_network_fails_startup_naming_the_setting()
    {
        using var factory = Factory(("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99"));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ForwardedHeaders:KnownNetworks", error.ToString());
    }

    [Fact]
    public async Task The_HTTPS_redirect_is_on_by_default()
    {
        await using var factory = Factory(("https_port", "443"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/test/client");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.StartsWith("https://", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Https_Redirect_false_turns_it_off_for_CloudFront_which_enforces_HTTPS_itself()
    {
        await using var factory = Factory(("https_port", "443"), ("Https:Redirect", "false"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/test/client");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
