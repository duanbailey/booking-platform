using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Test-only endpoints that apply the specs/07-nfr.md rules the way real endpoints
/// will, so T3 can test the 404-versus-403 rule end to end before they exist.
/// </summary>
[ApiController]
[Route("test")]
public sealed class AccessProbeController(AccessResolver access) : ControllerBase
{
    /// <summary>View a booking: customer, booking staff member or owner; others get 404.</summary>
    [HttpGet("bookings/{bookingId:guid}")]
    public async Task<IActionResult> GetBooking(Guid bookingId)
    {
        var booking = Access.Found(await access.ForBookingAsync(User.GetUserId(), bookingId));
        Access.Require(canView: booking.CanView, canAct: true);
        return Ok();
    }

    /// <summary>Change settings: anyone can view a business, only its owner may change it (403 otherwise).</summary>
    [HttpPatch("businesses/{businessId:guid}/settings")]
    public async Task<IActionResult> ChangeSettings(Guid businessId)
    {
        var roles = Access.Found(await access.ForBusinessAsync(User.GetUserId(), businessId));
        Access.Require(canView: true, canAct: roles.IsOwner);
        return NoContent();
    }
}
