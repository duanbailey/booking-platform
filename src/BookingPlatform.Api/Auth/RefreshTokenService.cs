using System.Buffers.Text;
using System.Security.Cryptography;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Auth;

public sealed record IssuedRefreshToken(string Token, DateTime ExpiresAt);

public sealed record RotatedRefreshToken(Guid UserId, IssuedRefreshToken Token);

/// <summary>
/// Refresh tokens (ADR 0010): random 256-bit values stored only as SHA-256 hashes,
/// single use, rotated on every refresh with a fresh 30-day expiry. Presenting a used
/// token revokes every token from that login (its family).
/// </summary>
public sealed class RefreshTokenService(BookingDbContext db, TimeProvider time, IOptions<JwtOptions> options)
{
    private readonly TimeSpan _lifetime = options.Value.RefreshTokenLifetime;

    /// <summary>Starts a login session (register, login or accept-invite).</summary>
    public async Task<IssuedRefreshToken> IssueForNewLoginAsync(Guid userId, CancellationToken ct = default)
    {
        var issued = Add(userId, familyId: Guid.NewGuid(), Now());
        await db.SaveChangesAsync(ct);
        return issued;
    }

    /// <summary>Retires the token and returns a new one from the same login.</summary>
    /// <exception cref="ApiException">InvalidRefreshToken if the token is unknown, expired, used or revoked.</exception>
    public async Task<RotatedRefreshToken> RotateAsync(string token, CancellationToken ct = default)
    {
        var now = Now();
        var hash = Hash(token);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The row lock makes two refreshes with the same token run one after the other,
        // so the second sees the token as used.
        var current = await db.RefreshTokens
            .FromSql($"SELECT * FROM refresh_tokens WHERE token_hash = {hash} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

        if (current is null) throw ApiException.InvalidRefreshToken();

        if (current.UsedAt is not null)
        {
            // Reuse: someone else holds this token too. End the whole login.
            await RevokeFamilyAsync(current.FamilyId, now, ct);
            await transaction.CommitAsync(ct);
            throw ApiException.InvalidRefreshToken();
        }

        if (current.RevokedAt is not null || current.ExpiresAt <= now) throw ApiException.InvalidRefreshToken();

        current.UsedAt = now;
        var issued = Add(current.UserId, current.FamilyId, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new RotatedRefreshToken(current.UserId, issued);
    }

    /// <summary>Logout: revokes every token from the token's login. Unknown tokens are ignored.</summary>
    public async Task RevokeLoginAsync(string token, CancellationToken ct = default)
    {
        var hash = Hash(token);
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId)
            .SingleOrDefaultAsync(ct);
        if (familyId is not null) await RevokeFamilyAsync(familyId.Value, Now(), ct);
    }

    private Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private IssuedRefreshToken Add(Guid userId, Guid familyId, DateTime now)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now + _lifetime;
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(token),
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });
        return new IssuedRefreshToken(token, expiresAt);
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private static string Hash(string token) => TokenHash.Of(token);
}
