using BookingPlatform.Api.Data;
using Microsoft.AspNetCore.Identity;

namespace BookingPlatform.Api.Auth;

public enum PasswordCheck { Invalid, Valid, ValidNeedsRehash }

/// <summary>
/// Password hashing with ASP.NET Core's PasswordHasher (PBKDF2-HMAC-SHA512, specs/07-nfr.md).
/// A check against no account, or an account without a password, still verifies against a
/// dummy hash, so every login failure takes about as long as a wrong password.
/// </summary>
public sealed class Passwords
{
    private static readonly User Nobody = new() { Email = "", Name = "", TimeZone = "UTC" };

    private readonly IPasswordHasher<User> _hasher;
    private readonly string _dummyHash;

    public Passwords(IPasswordHasher<User> hasher)
    {
        _hasher = hasher;
        _dummyHash = hasher.HashPassword(Nobody, Guid.NewGuid().ToString());
    }

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public PasswordCheck Check(User? user, string password)
    {
        if (user?.PasswordHash is null)
        {
            _hasher.VerifyHashedPassword(Nobody, _dummyHash, password);
            return PasswordCheck.Invalid;
        }

        return _hasher.VerifyHashedPassword(user, user.PasswordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Valid,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.ValidNeedsRehash,
            _ => PasswordCheck.Invalid,
        };
    }
}
