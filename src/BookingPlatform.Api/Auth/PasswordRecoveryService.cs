using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Auth;

/// <summary>
/// Password recovery and change (T7a; stories "User can recover a forgotten password" and
/// "User can change their password"; specs/05-data-model.md password_resets). The API inserts
/// recovery rows without a token; the worker (T26) sets the token hash and the 1-hour expiry
/// when it sends the email. Only a user's newest row works (by created_at, then the greater id),
/// once, before it expires. Every operation locks the user row first, so they never interleave.
/// </summary>
public sealed class PasswordRecoveryService(
    BookingDbContext db, Passwords passwords, AuthTokenService tokens, RefreshTokenService refreshTokens,
    OutboxWriter outbox, TimeProvider time)
{
    /// <summary>At most one recovery email per account in this period.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Queues a recovery email (emits PasswordResetRequested) if the email belongs to an account
    /// and its newest recovery row is at least <see cref="Cooldown"/> old. Returns nothing, so
    /// the caller can't tell which case applied (specs/07-nfr.md).
    /// </summary>
    public async Task RequestAsync(string email, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var userId = await db.Users.Where(u => u.Email == email) // case-insensitive collation
            .Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        if (userId is null) return;

        // Under the lock, two requests for one account run one after the other, so the second
        // sees the first one's row. A locking read: a plain one could miss a row committed while
        // this request waited (REPEATABLE READ snapshot).
        if (await db.LockUserAsync(userId.Value, ct) is null) return;
        var newestCreatedAt = await db.PasswordResets
            .FromSql($"SELECT * FROM password_resets WHERE user_id = {userId} ORDER BY created_at DESC, id DESC LIMIT 1 FOR UPDATE")
            .Select(r => (DateTime?)r.CreatedAt)
            .SingleOrDefaultAsync(ct);
        if (newestCreatedAt > now - Cooldown) return; // exactly 5 minutes old is outside the cooldown

        var reset = new PasswordReset { Id = Guid.NewGuid(), UserId = userId.Value, CreatedAt = now };
        db.PasswordResets.Add(reset);
        outbox.Add(new PasswordResetRequested(userId.Value, reset.Id));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Sets the password from a recovery link, marks the link and any pending invites used,
    /// revokes every refresh token and logs the user in. Emits PasswordChanged (Reset) only if
    /// the account had a password before (decided 2026-11-19).
    /// </summary>
    /// <exception cref="ApiException">ResetLinkInvalid, without saying why.</exception>
    public async Task<AuthTokenResponse> ResetAsync(string token, string password, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = TokenHash.Of(token);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var userId = await db.PasswordResets.Where(r => r.TokenHash == hash).Select(r => (Guid?)r.UserId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.ResetLinkInvalid();

        var user = await db.LockUserAsync(userId, ct) ?? throw ApiException.ResetLinkInvalid();
        // A locking read, as in InviteService.AcceptAsync: sees a row committed while this waited.
        var newest = await db.PasswordResets
            .FromSql($"SELECT * FROM password_resets WHERE user_id = {userId} ORDER BY created_at DESC, id DESC LIMIT 1 FOR UPDATE")
            .SingleAsync(ct);

        var valid = newest.TokenHash == hash // older links stop working once a newer row exists, sent or not
            && newest.UsedAt is null
            && newest.ExpiresAt > now; // the exact expiry moment is expired
        if (!valid) throw ApiException.ResetLinkInvalid();

        var hadPassword = user.PasswordHash is not null;
        user.PasswordHash = passwords.Hash(user, password);
        newest.UsedAt = now;
        if (hadPassword) outbox.Add(new PasswordChanged(userId, PasswordChangeMethod.Reset));
        await db.SaveChangesAsync(ct);
        // An account without a password accepts its invite this way: invite links stop working.
        await db.UserInvites
            .Where(i => i.UserId == userId && i.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, now), ct);
        await refreshTokens.RevokeAllForUserAsync(userId, ct);

        var body = await tokens.StartLoginAsync(userId, ct);
        await transaction.CommitAsync(ct);
        return body;
    }

    /// <summary>
    /// Changes the password of a logged-in user, revokes every refresh token and returns new
    /// tokens. Emits PasswordChanged (Changed). Recovery links keep working (decided 2026-11-19).
    /// </summary>
    /// <exception cref="ApiException">
    /// Unauthenticated if the user no longer exists; CurrentPasswordIncorrect if the current
    /// password is wrong (an empty one is simply wrong).
    /// </exception>
    public async Task<AuthTokenResponse> ChangeAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Under the lock, a second change with the same current password checks against the new one.
        var user = await db.LockUserAsync(userId, ct) ?? throw ApiException.Unauthenticated();
        if (passwords.Check(user, currentPassword) == PasswordCheck.Invalid) throw ApiException.CurrentPasswordIncorrect();

        user.PasswordHash = passwords.Hash(user, newPassword);
        outbox.Add(new PasswordChanged(userId, PasswordChangeMethod.Changed));
        await db.SaveChangesAsync(ct);
        await refreshTokens.RevokeAllForUserAsync(userId, ct);

        var body = await tokens.StartLoginAsync(userId, ct);
        await transaction.CommitAsync(ct);
        return body;
    }
}
