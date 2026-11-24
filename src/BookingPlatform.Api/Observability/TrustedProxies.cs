using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace BookingPlatform.Api.Observability;

/// <summary>
/// Which proxies may set the client IP and scheme (T27a, decided 2026-11-24). Loopback is always trusted (ASP.NET's
/// default); ForwardedHeaders:KnownNetworks adds CIDR ranges (behind CloudFront and the internal load balancer: the
/// VPC), and ForwardedHeaders:ForwardLimit is how many hops to walk back (default 1; 2 for CloudFront plus the load
/// balancer). Rate limiting (specs/07-nfr.md) partitions by the resulting IP. A malformed setting fails startup.
/// </summary>
public static class TrustedProxies
{
    public static ForwardedHeadersOptions Options(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        var section = configuration.GetSection("ForwardedHeaders");
        foreach (var value in section.GetSection("KnownNetworks").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (!System.Net.IPNetwork.TryParse(value!.Trim(), out var network))
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks has an entry that is not a CIDR range: '{value}'.");
            options.KnownIPNetworks.Add(network);
        }

        if (section["ForwardLimit"] is { Length: > 0 } limit)
        {
            options.ForwardLimit = int.TryParse(limit, out var hops) && hops >= 1
                ? hops
                : throw new InvalidOperationException("ForwardedHeaders:ForwardLimit must be a whole number of at least 1.");
        }
        return options;
    }
}
