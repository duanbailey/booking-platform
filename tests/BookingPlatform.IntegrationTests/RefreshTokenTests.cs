using System.Security.Cryptography;
using System.Text;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Errors;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T3: refresh tokens (ADR 0010, specs/05-data-model.md refresh_tokens): 30-day sliding
/// expiry, single use with rotation, reuse revokes the whole login, logout revokes it too.
/// </summary>
public class RefreshTokenTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private readonly Sql _sql = new(database.ConnectionString);
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    private RefreshTokenService Service() => new(database.CreateContext(), _time, Options.Create(new JwtOptions()));

    private static async Task<ApiException> AssertInvalid(Task task)
    {
        var error = await Assert.ThrowsAsync<ApiException>(() => task);
        Assert.Equal(401, error.Status);
        Assert.Equal(ErrorCode.InvalidRefreshToken, error.Code);
        return error;
    }

    [Fact]
    public async Task Issued_token_is_stored_only_as_its_SHA256_hash_and_expires_in_30_days()
    {
        var user = await _sql.InsertUserAsync();

        var issued = await Service().IssueForNewLoginAsync(user);

        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(30), issued.ExpiresAt);
        Assert.Equal(0L, Convert.ToInt64(await _sql.ScalarAsync(
            "SELECT COUNT(*) FROM refresh_tokens WHERE token_hash = @raw", new { raw = issued.Token })));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Token)));
        Assert.Equal(user, Guid.Parse(Convert.ToString(await _sql.ScalarAsync(
            "SELECT user_id FROM refresh_tokens WHERE token_hash = @hash", new { hash }))!));
    }

    [Fact]
    public async Task Tokens_are_random_and_long()
    {
        var user = await _sql.InsertUserAsync();
        var a = await Service().IssueForNewLoginAsync(user);
        var b = await Service().IssueForNewLoginAsync(user);

        Assert.NotEqual(a.Token, b.Token);
        Assert.True(a.Token.Length >= 43, "at least 256 bits, base64url-encoded");
    }

    [Fact]
    public async Task Rotation_returns_a_new_token_for_the_same_user_with_a_fresh_30_days()
    {
        var user = await _sql.InsertUserAsync();
        var first = await Service().IssueForNewLoginAsync(user);
        _time.Advance(TimeSpan.FromDays(20));

        var rotated = await Service().RotateAsync(first.Token);

        Assert.Equal(user, rotated.UserId);
        Assert.NotEqual(first.Token, rotated.Token.Token);
        // Sliding expiry: 30 days from the refresh, not from the login.
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(30), rotated.Token.ExpiresAt);
    }

    [Fact]
    public async Task Rotated_token_can_itself_be_rotated()
    {
        var user = await _sql.InsertUserAsync();
        var first = await Service().IssueForNewLoginAsync(user);
        var second = await Service().RotateAsync(first.Token);

        var third = await Service().RotateAsync(second.Token.Token);

        Assert.Equal(user, third.UserId);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_fails_and_revokes_every_token_from_that_login()
    {
        var user = await _sql.InsertUserAsync();
        var first = await Service().IssueForNewLoginAsync(user);
        var second = await Service().RotateAsync(first.Token);

        await AssertInvalid(Service().RotateAsync(first.Token));

        // The newest token from the same login no longer works either.
        await AssertInvalid(Service().RotateAsync(second.Token.Token));
    }

    [Fact]
    public async Task Reuse_does_not_affect_the_users_other_logins()
    {
        var user = await _sql.InsertUserAsync();
        var phone = await Service().IssueForNewLoginAsync(user);
        var laptop = await Service().IssueForNewLoginAsync(user);
        await Service().RotateAsync(phone.Token);

        await AssertInvalid(Service().RotateAsync(phone.Token));

        Assert.Equal(user, (await Service().RotateAsync(laptop.Token)).UserId);
    }

    [Fact]
    public async Task Token_is_valid_until_its_expiry_and_not_at_or_after_it()
    {
        var user = await _sql.InsertUserAsync();
        var a = await Service().IssueForNewLoginAsync(user);
        var b = await Service().IssueForNewLoginAsync(user);

        _time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        await Service().RotateAsync(a.Token);

        _time.Advance(TimeSpan.FromSeconds(1));
        await AssertInvalid(Service().RotateAsync(b.Token));
    }

    [Fact]
    public async Task Unknown_token_is_invalid()
    {
        await AssertInvalid(Service().RotateAsync("no-such-token"));
    }

    [Fact]
    public async Task Logout_revokes_every_token_from_that_login_only()
    {
        var user = await _sql.InsertUserAsync();
        var first = await Service().IssueForNewLoginAsync(user);
        var second = await Service().RotateAsync(first.Token);
        var other = await Service().IssueForNewLoginAsync(user);

        await Service().RevokeLoginAsync(second.Token.Token);

        await AssertInvalid(Service().RotateAsync(second.Token.Token));
        Assert.Equal(user, (await Service().RotateAsync(other.Token)).UserId);
    }

    [Fact]
    public async Task Logout_with_an_unknown_or_already_used_token_succeeds_silently()
    {
        var user = await _sql.InsertUserAsync();
        var first = await Service().IssueForNewLoginAsync(user);
        await Service().RotateAsync(first.Token);

        await Service().RevokeLoginAsync("no-such-token");
        await Service().RevokeLoginAsync(first.Token); // used: revokes the login, no error
        await Service().RevokeLoginAsync(first.Token); // already revoked: still no error
    }

    [Fact]
    public async Task Refresh_waits_for_the_row_lock_and_then_sees_the_token_as_used()
    {
        // ADR 0010: two refreshes with one token must run one after the other, so the
        // second sees the first's used_at and is treated as reuse. Another transaction
        // stands in for the first refresh: it locks the row and marks it used, uncommitted.
        var user = await _sql.InsertUserAsync();
        var token = await Service().IssueForNewLoginAsync(user);
        var hash = Hash(token.Token);

        await using var first = new MySqlConnection(database.ConnectionString);
        await first.OpenAsync();
        await using var firstTx = await first.BeginTransactionAsync();
        await using (var lockRow = new MySqlCommand(
            "SELECT id FROM refresh_tokens WHERE token_hash = @hash FOR UPDATE; " +
            "UPDATE refresh_tokens SET used_at = @now WHERE token_hash = @hash", first, firstTx))
        {
            lockRow.Parameters.AddWithValue("@hash", hash);
            lockRow.Parameters.AddWithValue("@now", _time.GetUtcNow().UtcDateTime);
            await lockRow.ExecuteNonQueryAsync();
        }

        var second = Service().RotateAsync(token.Token);

        // MySQL reports the second refresh as blocked on its locking read of refresh_tokens.
        var waitingStatement = await WaitForLockWaitOnRefreshTokensAsync();
        Assert.Contains("FOR UPDATE", waitingStatement);
        Assert.False(second.IsCompleted);

        await firstTx.CommitAsync();

        // A plain read would have seen the last committed row (not yet used) and succeeded.
        await AssertInvalid(second);
        Assert.Equal(0L, Convert.ToInt64(await _sql.ScalarAsync(
            "SELECT COUNT(*) FROM refresh_tokens WHERE token_hash = @hash AND revoked_at IS NULL", new { hash })));
    }

    /// <summary>
    /// Polls until a statement is waiting for a row lock on this test database's refresh_tokens,
    /// and returns its SQL. Uses performance_schema, which is live; information_schema.innodb_trx
    /// is a cache refreshed only after 0.1 s without reads, so frequent polling never sees a change.
    /// </summary>
    private async Task<string> WaitForLockWaitOnRefreshTokensAsync()
    {
        const string waitingStatement =
            "SELECT s.SQL_TEXT FROM performance_schema.data_lock_waits w " +
            "JOIN performance_schema.data_locks l ON l.ENGINE_LOCK_ID = w.REQUESTING_ENGINE_LOCK_ID " +
            "JOIN performance_schema.events_statements_current s ON s.THREAD_ID = w.REQUESTING_THREAD_ID " +
            "WHERE l.OBJECT_SCHEMA = DATABASE() AND l.OBJECT_NAME = 'refresh_tokens' LIMIT 1";

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await _sql.ScalarAsync(waitingStatement) is string sql) return sql;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException("The refresh never waited for a row lock on refresh_tokens.");
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ------------------------------------------------------------ Schema

    [Fact]
    public async Task Duplicate_token_hash_is_rejected()
    {
        var user = await _sql.InsertUserAsync();
        const string insert =
            "INSERT INTO refresh_tokens (id, user_id, family_id, token_hash, expires_at, created_at, used_at, revoked_at) " +
            "VALUES (@id, @user, @family, 'same-hash', @now, @now, NULL, NULL)";
        await _sql.ExecuteAsync(insert, new { id = Guid.NewGuid(), user, family = Guid.NewGuid(), now = Sql.Now });

        Assert.Equal(ExpectedError.DuplicateKey, await _sql.FailsWithAsync(insert,
            new { id = Guid.NewGuid(), user, family = Guid.NewGuid(), now = Sql.Now }));
    }

    [Fact]
    public async Task Refresh_token_for_an_unknown_user_is_rejected()
    {
        Assert.Equal(ExpectedError.ForeignKeyViolation, await _sql.FailsWithAsync(
            "INSERT INTO refresh_tokens (id, user_id, family_id, token_hash, expires_at, created_at) " +
            "VALUES (@id, @user, @family, 'h', @now, @now)",
            new { id = Guid.NewGuid(), user = Guid.NewGuid(), family = Guid.NewGuid(), now = Sql.Now }));
    }
}
