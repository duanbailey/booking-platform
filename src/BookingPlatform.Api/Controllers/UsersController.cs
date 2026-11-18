using BookingPlatform.Api.Auth;
using Microsoft.AspNetCore.Mvc;

namespace BookingPlatform.Api.Controllers;

/// <summary>Operations on another user's account (specs/04-api.openapi.yaml).</summary>
[ApiController]
[Route("v1/users")]
public sealed class UsersController(InviteService invites) : ControllerBase
{
    /// <summary>
    /// Story "Invited user sets their password" (resend scenarios): an admin for an owner,
    /// an owner for their staff (specs/07-nfr.md). 404 for anyone else, before 422.
    /// </summary>
    [HttpPost("{userId:guid}/resend-invite")]
    public async Task<IActionResult> ResendInvite(Guid userId, CancellationToken ct)
    {
        await invites.ResendAsync(User.GetUserId(), userId, ct);
        return NoContent();
    }
}
