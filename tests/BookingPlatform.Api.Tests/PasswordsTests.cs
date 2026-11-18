using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Data;
using Microsoft.AspNetCore.Identity;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T4: password checks (specs/07-nfr.md). Every login failure does the same hashing work,
/// so an unknown email or an account without a password can't be told apart by timing.
/// </summary>
public class PasswordsTests
{
    private sealed class CountingHasher : IPasswordHasher<User>
    {
        private readonly PasswordHasher<User> _real = new();
        public int Verifications;

        public string HashPassword(User user, string password) => _real.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            Verifications++;
            return _real.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private static User UserWith(string? hash) => new() { Email = "a@example.test", Name = "A", TimeZone = "UTC", PasswordHash = hash };

    [Fact]
    public void Right_password_is_accepted_and_wrong_one_refused()
    {
        var passwords = new Passwords(new CountingHasher());
        var user = UserWith(null);
        user.PasswordHash = passwords.Hash(user, "correct horse");

        Assert.Equal(PasswordCheck.Valid, passwords.Check(user, "correct horse"));
        Assert.Equal(PasswordCheck.Invalid, passwords.Check(user, "wrong horse"));
    }

    [Fact]
    public void Hash_is_salted()
    {
        var passwords = new Passwords(new CountingHasher());
        var user = UserWith(null);

        Assert.NotEqual(passwords.Hash(user, "same password"), passwords.Hash(user, "same password"));
    }

    [Fact]
    public void Unknown_user_and_user_without_a_password_still_run_one_hash_check()
    {
        var hasher = new CountingHasher();
        var passwords = new Passwords(hasher);

        Assert.Equal(PasswordCheck.Invalid, passwords.Check(null, "anything"));
        Assert.Equal(1, hasher.Verifications);
        Assert.Equal(PasswordCheck.Invalid, passwords.Check(UserWith(null), "anything"));
        Assert.Equal(2, hasher.Verifications);
    }

    [Fact]
    public void Hash_from_an_older_format_is_flagged_for_upgrade()
    {
        var oldHasher = new PasswordHasher<User>(Microsoft.Extensions.Options.Options.Create(
            new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
        var user = UserWith(null);
        user.PasswordHash = oldHasher.HashPassword(user, "correct horse");

        Assert.Equal(PasswordCheck.ValidNeedsRehash, new Passwords(new CountingHasher()).Check(user, "correct horse"));
    }
}
