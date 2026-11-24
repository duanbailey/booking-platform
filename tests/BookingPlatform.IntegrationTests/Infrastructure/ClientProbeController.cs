using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Test-only endpoint (T27a): echoes the client IP and scheme the API ends up with after the forwarded-headers
/// middleware, which rate limiting and the HTTPS redirect act on.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("test/client")]
public sealed class ClientProbeController : ControllerBase
{
    public sealed record Probe(string? Ip, string Scheme);

    [HttpGet]
    public Probe Get() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Scheme);
}
