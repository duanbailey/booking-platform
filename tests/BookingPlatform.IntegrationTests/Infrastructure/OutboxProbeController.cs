using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Events;
using Microsoft.AspNetCore.Mvc;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Test-only endpoint that writes an event through the real request pipeline, so T5 can
/// check the request's correlation ID reaches the outbox before real endpoints emit events.
/// </summary>
[ApiController]
[Route("test/outbox")]
public sealed class OutboxProbeController(BookingDbContext db, OutboxWriter outbox) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Write()
    {
        var message = outbox.Add(new PasswordChanged(User.GetUserId(), PasswordChangeMethod.Changed));
        await db.SaveChangesAsync();
        return Ok(new { eventId = message.Id });
    }
}
