using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BookingPlatform.Api.Tests.Infrastructure;

/// <summary>
/// Test-only endpoints that exercise the cross-cutting pipeline (T3) before
/// any real endpoint exists. Registered only by <see cref="ApiFactory"/>.
/// </summary>
[ApiController]
[Route("test")]
public sealed class ProbeController(ILogger<ProbeController> logger) : ControllerBase
{
    public sealed record ProbeRequest(
        [Required, EmailAddress] string? Email,
        [Range(1, 10)] int Count);

    [HttpGet("me")]
    public IActionResult Me() => Ok(new { userId = User.GetUserId() });

    [AllowAnonymous]
    [HttpGet("anonymous")]
    public IActionResult Anonymous()
    {
        logger.LogInformation("Probe endpoint reached");
        return Ok();
    }

    [AllowAnonymous]
    [HttpPost("validate")]
    public IActionResult Validate(ProbeRequest request) => Ok(request);

    [HttpGet("forbidden")]
    public IActionResult Forbidden() => throw ApiException.Forbidden();

    [HttpGet("not-found")]
    public IActionResult Missing() => throw ApiException.NotFound();

    [AllowAnonymous]
    [HttpGet("rule-error")]
    public IActionResult RuleError() =>
        throw new ApiException(StatusCodes.Status422UnprocessableEntity, ErrorCode.MinimumNotice,
            "Too close to the start.", "R3", new { minNoticeMinutes = 120 });

    [AllowAnonymous]
    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException("secret detail: password=hunter2");

    /// <summary>Sets headers meant for a success response, then fails.</summary>
    [AllowAnonymous]
    [HttpGet("boom-after-headers")]
    public IActionResult BoomAfterHeaders([FromQuery] bool expected)
    {
        Response.Headers.Location = "/test/somewhere";
        Response.Headers.ETag = "\"v1\"";
        Response.Headers.CacheControl = "public, max-age=3600";
        if (expected) throw ApiException.Forbidden();
        throw new InvalidOperationException("failed after setting headers");
    }

    [AllowAnonymous]
    [HttpGet("query")]
    public IActionResult Query([FromQuery] DateOnly from, [FromQuery, Range(1, 10)] int limit = 5) => Ok();

    public enum Colour { Red, Green }

    public sealed record EnumRequest(Colour Colour);

    [AllowAnonymous]
    [HttpPost("enum")]
    public IActionResult Enum(EnumRequest request) => Ok(request);
}
