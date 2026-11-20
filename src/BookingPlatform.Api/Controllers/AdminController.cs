using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace BookingPlatform.Api.Controllers;

public sealed record CreateOwnerRequest(
    [Required, PersonName] string OwnerName,
    [Required, Email] string OwnerEmail,
    [Required, BusinessName] string BusinessName,
    [Required, IanaTimeZone] string BusinessTimeZone);

/// <summary>
/// Admin operations (specs/07-nfr.md: an admin can create owners and resend an owner's
/// invite, and nothing else). The admin role itself is granted only by the operator
/// script (<see cref="Admin.AdminCli"/>), never through the API.
/// </summary>
[ApiController]
[Route("v1/admin")]
public sealed class AdminController(
    BookingDbContext db, AccessResolver access, OutboxWriter outbox, TimeProvider time) : ControllerBase
{
    private const int MySqlDuplicateKey = 1062;

    /// <summary>
    /// Story "Admin creates an owner and their business". A new email gets an account
    /// without a password and an invite; an existing account without a password gets a new
    /// invite; an existing account with one gets an ownership notice. Emits AccessGranted.
    /// </summary>
    [HttpPost("owners")]
    public async Task<IActionResult> CreateOwner(CreateOwnerRequest request, CancellationToken ct)
    {
        var adminId = User.GetUserId();
        if (!await access.IsAdminAsync(adminId, ct)) throw ApiException.Forbidden();

        var now = time.GetUtcNow().UtcDateTime;
        var email = request.OwnerEmail.Trim();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // case-insensitive collation. An existing account is then locked (as accept-invite and
        // resend-invite do, T7), so its password can't be set between this check and the invite.
        // A new email isn't locked: the unique index on users.email settles that race.
        var existingId = await db.Users.Where(u => u.Email == email).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        var owner = existingId is null ? null : await db.LockUserAsync(existingId.Value, ct);
        if (owner is null)
        {
            owner = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                Name = request.OwnerName.Trim(),
                TimeZone = IanaTimeZoneAttribute.Canonicalize(request.BusinessTimeZone),
                CreatedAt = now,
            };
            db.Users.Add(owner);
        }
        else if (await db.Businesses.AnyAsync(b => b.OwnerUserId == owner.Id, ct))
        {
            throw ApiException.AlreadyOwner();
        }

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = request.BusinessName.Trim(),
            OwnerUserId = owner.Id,
            TimeZone = IanaTimeZoneAttribute.Canonicalize(request.BusinessTimeZone),
            CreatedAt = now,
        };
        db.Businesses.Add(business);

        // No password yet (new or never activated): a new invite, so the owner can log in.
        // Only the newest invite works (T7); the worker sets its token and expiry.
        UserInvite? invite = null;
        if (owner.PasswordHash is null)
        {
            invite = new UserInvite { Id = Guid.NewGuid(), UserId = owner.Id, CreatedBy = adminId, CreatedAt = now };
            db.UserInvites.Add(invite);
        }

        outbox.Add(new AccessGranted(owner.Id, business.Id, GrantedRole.Owner, invite?.Id, adminId));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is MySqlException { Number: MySqlDuplicateKey })
        {
            // A simultaneous request for the same email won: either users.email (it created
            // the account) or businesses.owner_user_id (it made the account an owner).
            throw ApiException.AlreadyOwner();
        }
        await transaction.CommitAsync(ct);

        return Created($"/v1/businesses/{business.Id}", BusinessResponse.From(business));
    }
}
