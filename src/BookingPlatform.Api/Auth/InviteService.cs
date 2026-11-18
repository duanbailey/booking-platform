using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Auth;

/// <summary>
/// Set-password invites (story "Invited user sets their password"; specs/05-data-model.md
/// user_invites). The API creates invite rows without a token; the worker (T26) sets the
/// token hash and the 7-day expiry when it sends the email. Only a user's newest invite
/// works (by created_at, then the greater id), once, before it expires, and only while the
/// user has no password. Accepting and resending lock the user row, so they never interleave.
/// </summary>
public sealed class InviteService(
    BookingDbContext db, Passwords passwords, AuthTokenService tokens, AccessResolver access,
    OutboxWriter outbox, TimeProvider time)
{
    /// <summary>Sets the password from an invite link and logs the user in.</summary>
    /// <exception cref="ApiException">InviteInvalid, without saying why.</exception>
    public async Task<AuthTokenResponse> AcceptAsync(string token, string password, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = TokenHash.Of(token);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var userId = await db.UserInvites.Where(i => i.TokenHash == hash).Select(i => (Guid?)i.UserId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.InviteInvalid();

        // Everything below is read under the lock: a resend or a second accept for this user
        // waits here, so it sees this accept's result (and this accept sees a newer invite).
        var user = await db.LockUserAsync(userId, ct) ?? throw ApiException.InviteInvalid();
        // A locking read: a plain one would use the snapshot taken by the lookup above
        // (REPEATABLE READ) and miss an invite committed while this request waited for the lock.
        var newest = await db.UserInvites
            .FromSql($"SELECT * FROM user_invites WHERE user_id = {userId} ORDER BY created_at DESC, id DESC LIMIT 1 FOR UPDATE")
            .AsNoTracking()
            .SingleAsync(ct);

        var valid = newest.TokenHash == hash // older links stop working once a newer invite exists
            && newest.UsedAt is null
            && newest.ExpiresAt > now // the exact expiry moment is expired
            && user.PasswordHash is null;
        if (!valid) throw ApiException.InviteInvalid();

        user.PasswordHash = passwords.Hash(user, password);
        await db.SaveChangesAsync(ct);
        // Every pending invite is used now, so no pending row is left for a user with a password.
        await db.UserInvites
            .Where(i => i.UserId == userId && i.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, now), ct);

        var body = await tokens.StartLoginAsync(userId, ct);
        await transaction.CommitAsync(ct);
        return body;
    }

    /// <summary>
    /// Queues a new invite for a user without a password (emits AccessGranted). An admin
    /// resends for a user who owns a business (role Owner, that business); otherwise an owner
    /// resends for active staff in their business (role Staff). The Owner path wins if both apply.
    /// </summary>
    /// <exception cref="ApiException">
    /// NotFound if the caller may not resend this user's invite (checked first, so the reply
    /// never reveals the account or its password); InviteNotPending if the user has a password.
    /// </exception>
    public async Task ResendAsync(Guid callerId, Guid userId, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var (businessId, role) = await ResendGrantAsync(callerId, userId, ct) ?? throw ApiException.NotFound();

        var user = await db.LockUserAsync(userId, ct) ?? throw ApiException.NotFound();
        if (user.PasswordHash is not null) throw ApiException.InviteNotPending();

        var invite = new UserInvite
        {
            Id = Guid.NewGuid(), UserId = userId, CreatedBy = callerId, CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.UserInvites.Add(invite);
        outbox.Add(new AccessGranted(userId, businessId, role, invite.Id, callerId));

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The business and role a resend by this caller is for, or null if they may not resend it.</summary>
    private async Task<(Guid BusinessId, GrantedRole Role)?> ResendGrantAsync(Guid callerId, Guid userId, CancellationToken ct)
    {
        if (await access.IsAdminAsync(callerId, ct))
        {
            var ownedByUser = await db.Businesses.Where(b => b.OwnerUserId == userId)
                .Select(b => (Guid?)b.Id).SingleOrDefaultAsync(ct);
            if (ownedByUser is not null) return (ownedByUser.Value, GrantedRole.Owner);
        }

        var staffInCallersBusiness = await (
                from b in db.Businesses
                join s in db.Staff on b.Id equals s.BusinessId
                where b.OwnerUserId == callerId && s.UserId == userId && s.RemovedAt == null
                select (Guid?)b.Id)
            .SingleOrDefaultAsync(ct);
        return staffInCallersBusiness is null ? null : (staffInCallersBusiness.Value, GrantedRole.Staff);
    }
}
