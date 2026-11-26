using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T7a: POST /v1/auth/change-password. Story "User can change their password": with the
/// right current password the password is updated, every refresh token is revoked, the
/// caller gets new tokens and PasswordChanged (method Changed) is emitted; with a wrong one
/// (including empty), 422 CurrentPasswordIncorrect and nothing changes. Recovery links keep
/// working (decided 2026-11-19). The clock is frozen.
/// </summary>
public class ChangePasswordTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private const string Path = "/v1/auth/change-password";
    private const string NewPassword = "a brand new password";

    private Task<HttpResponseMessage> Change(string accessToken, string currentPassword, string newPassword = NewPassword) =>
        fixture.ClientWithToken(accessToken).PostJsonAsync(Path, new { currentPassword, newPassword });

    private Task<HttpResponseMessage> Login(string email, string password) =>
        fixture.Api.CreateClient().PostJsonAsync("/v1/auth/login", new { email, password });

    private Task<HttpResponseMessage> Refresh(string refreshToken) => fixture.Api.RefreshAsync(refreshToken);

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private async Task<string?> PasswordHashAsync(Guid userId) =>
        await fixture.Sql.ScalarAsync("SELECT password_hash FROM users WHERE id = @userId", new { userId }) is string hash
            ? hash : null;

    private Task<long> ActiveRefreshTokensAsync(Guid userId) =>
        CountAsync("SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId AND revoked_at IS NULL", new { userId });

    private Task<long> EventCountAsync(Guid userId) =>
        CountAsync("SELECT COUNT(*) FROM outbox WHERE event_type = 'PasswordChanged' AND message_group_id = @id",
            new { id = userId.ToString() });

    /// <summary>A registered user, logged in twice (two devices).</summary>
    private async Task<(Guid UserId, string Email, string AccessToken, string RefreshToken, string OtherRefreshToken)> UserAsync()
    {
        var email = ApiFixture.NewEmail();
        var (registered, registeredRefresh) = await fixture.RegisterWithRefreshAsync(email);
        var otherRefresh = RefreshCookie.TokenFrom(await Login(email, ApiFixture.Password));
        var userId = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM users WHERE email = @email", new { email }))!);
        return (userId, email, registered.GetProperty("accessToken").GetString()!,
            registeredRefresh, otherRefresh);
    }

    private async Task AssertCurrentPasswordIncorrectAndNothingChanged(string currentPassword)
    {
        var (userId, email, access, refresh, _) = await UserAsync();
        var hashBefore = await PasswordHashAsync(userId);

        await Json.ErrorAsync(await Change(access, currentPassword), HttpStatusCode.UnprocessableEntity, "CurrentPasswordIncorrect");

        Assert.Equal(hashBefore, await PasswordHashAsync(userId));
        Assert.Equal(2, await ActiveRefreshTokensAsync(userId));
        Assert.Equal(0, await EventCountAsync(userId));
        await Json.ReadAsync(await Refresh(refresh), HttpStatusCode.OK);
        await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Right_current_password_updates_it_and_returns_new_tokens()
    {
        var (userId, email, access, _, _) = await UserAsync();

        var response = await Change(access, ApiFixture.Password);
        var tokens = await Json.ReadAsync(response, HttpStatusCode.OK);

        Assert.Equal("Bearer", tokens.GetProperty("tokenType").GetString());
        var me = await Json.ReadAsync(
            await fixture.ClientWithToken(tokens.GetProperty("accessToken").GetString()!).GetAsync("/v1/me"), HttpStatusCode.OK);
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
        await Json.ReadAsync(await Refresh(RefreshCookie.TokenFrom(response)), HttpStatusCode.OK);

        await Json.ReadAsync(await Login(email, NewPassword), HttpStatusCode.OK);
        await Json.ErrorAsync(await Login(email, ApiFixture.Password), HttpStatusCode.Unauthorized, "InvalidCredentials");
    }

    [Fact]
    public async Task Change_revokes_every_earlier_refresh_token_of_the_user()
    {
        var (userId, _, access, refresh, otherRefresh) = await UserAsync();

        var newRefresh = RefreshCookie.TokenFrom(await Change(access, ApiFixture.Password));

        await Json.ErrorAsync(await Refresh(refresh), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        await Json.ErrorAsync(await Refresh(otherRefresh), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        Assert.Equal(2, await CountAsync(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId AND revoked_at = @now AND token_hash <> @newHash",
            new { userId, now = fixture.Now, newHash = Sql.TokenHash(newRefresh) }));
        Assert.Equal(1, await ActiveRefreshTokensAsync(userId));
    }

    [Fact]
    public async Task Change_emits_PasswordChanged_with_method_Changed()
    {
        var (userId, _, access, _, _) = await UserAsync();

        await Json.ReadAsync(await Change(access, ApiFixture.Password), HttpStatusCode.OK);

        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE event_type = 'PasswordChanged' AND message_group_id = @id",
            new { id = userId.ToString() }))!;
        EventSchemas.AssertValid(payload);
        var data = JsonDocument.Parse(payload).RootElement.GetProperty("data");
        Assert.Equal(userId, data.GetProperty("userId").GetGuid());
        Assert.Equal("Changed", data.GetProperty("method").GetString());
    }

    [Fact]
    public Task Wrong_current_password_is_422_CurrentPasswordIncorrect_and_nothing_changes() =>
        AssertCurrentPasswordIncorrectAndNothingChanged("not my password");

    [Fact]
    public Task Empty_current_password_counts_as_wrong() =>
        AssertCurrentPasswordIncorrectAndNothingChanged("");

    [Fact]
    public async Task New_password_may_equal_the_current_one()
    {
        var (userId, email, access, refresh, _) = await UserAsync();

        await Json.ReadAsync(await Change(access, ApiFixture.Password, ApiFixture.Password), HttpStatusCode.OK);

        await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);
        await Json.ErrorAsync(await Refresh(refresh), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        Assert.Equal(1, await EventCountAsync(userId));
    }

    [Fact]
    public async Task Pending_recovery_link_keeps_working_after_a_change()
    {
        var (userId, email, access, _, _) = await UserAsync();
        var token = Guid.NewGuid().ToString("N");
        await fixture.Sql.InsertPasswordResetAsync(userId, token, expiresAt: fixture.Now.AddHours(1), createdAt: fixture.Now);

        await Json.ReadAsync(await Change(access, ApiFixture.Password), HttpStatusCode.OK);

        await Json.ReadAsync(await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/reset-password",
            new { token, password = "the recovered password" }), HttpStatusCode.OK);
        await Json.ReadAsync(await Login(email, "the recovered password"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_simultaneous_changes_with_the_same_current_password_give_one_success_and_one_422()
    {
        // The user row lock makes the second change check against the first one's new password.
        var (userId, email, access, _, _) = await UserAsync();

        var responses = await Task.WhenAll(
            Change(access, ApiFixture.Password, "first password!"), Change(access, ApiFixture.Password, "second password!"));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await EventCountAsync(userId));
        Assert.Equal(1, await ActiveRefreshTokensAsync(userId));
        var logins = await Task.WhenAll(Login(email, "first password!"), Login(email, "second password!"));
        Assert.Single(logins, r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_waits_for_the_user_row_lock_and_then_sees_its_token_revoked()
    {
        // T7a: a change or reset holding the user lock revokes every refresh token; a refresh
        // waiting meanwhile must then be refused. (Without the user lock, a refresh and the
        // revoke-all lock token rows in different orders and can deadlock.)
        var (userId, _, _, refresh, _) = await UserAsync();
        Task<HttpResponseMessage> refreshing;
        await using (var held = await fixture.Sql.LockUserRowAsync(userId))
        {
            refreshing = Refresh(refresh);
            await fixture.Sql.WaitForLockWaitAsync("users");
            Assert.False(refreshing.IsCompleted);
            await held.ExecuteAsync(
                "UPDATE refresh_tokens SET revoked_at = @now WHERE user_id = @userId AND revoked_at IS NULL",
                new { userId, now = fixture.Now });
            await held.CommitAsync();
        }

        await Json.ErrorAsync(await refreshing, HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        Assert.Equal(0, await ActiveRefreshTokensAsync(userId));
    }

    [Fact]
    public async Task Change_waits_for_a_refresh_in_progress_and_revokes_the_token_it_issued()
    {
        // T7a: another transaction stands in for a refresh: it holds the user row in share mode
        // (as RefreshTokenService does) and inserts the rotated token, uncommitted.
        var (userId, _, access, _, _) = await UserAsync();
        var rotatedHash = Sql.TokenHash(Guid.NewGuid().ToString());
        Task<HttpResponseMessage> change;
        await using (var refreshing = await fixture.Sql.LockUserRowAsync(userId, shared: true))
        {
            await refreshing.ExecuteAsync(
                "INSERT INTO refresh_tokens (id, user_id, family_id, token_hash, expires_at, created_at) " +
                "VALUES (@id, @userId, @familyId, @rotatedHash, @expiresAt, @now)",
                new
                {
                    id = Guid.NewGuid(), userId, familyId = Guid.NewGuid(), rotatedHash,
                    expiresAt = fixture.Now.AddDays(30), now = fixture.Now,
                });
            change = Change(access, ApiFixture.Password);
            await fixture.Sql.WaitForLockWaitAsync("users");
            Assert.False(change.IsCompleted);
            await refreshing.CommitAsync();
        }

        await Json.ReadAsync(await change, HttpStatusCode.OK);
        Assert.Equal(fixture.Now, (DateTime)(await fixture.Sql.ScalarAsync(
            "SELECT revoked_at FROM refresh_tokens WHERE token_hash = @rotatedHash", new { rotatedHash }))!);
        Assert.Equal(1, await ActiveRefreshTokensAsync(userId)); // only the change's own
    }

    [Fact]
    public async Task Without_an_access_token_it_is_401_Unauthenticated()
    {
        var response = await fixture.Api.CreateClient().PostJsonAsync(Path,
            new { currentPassword = ApiFixture.Password, newPassword = NewPassword });

        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Access_token_of_an_unknown_user_is_401_Unauthenticated()
    {
        var response = await fixture.Api.ClientFor(Guid.NewGuid()).PostJsonAsync(Path,
            new { currentPassword = ApiFixture.Password, newPassword = NewPassword });

        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Theory]
    [InlineData("currentPassword", """{"newPassword":"a long enough password"}""")]
    [InlineData("newPassword", """{"currentPassword":"x"}""")]
    [InlineData("newPassword", """{"currentPassword":"x","newPassword":"short"}""")]
    public async Task Invalid_field_returns_400_ValidationFailed_naming_it(string field, string json)
    {
        var (_, _, access, _, _) = await UserAsync();

        var error = await Json.ErrorAsync(await fixture.ClientWithToken(access).PostAsync(Path, Json.Body(json)),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Fact]
    public async Task New_password_longer_than_128_characters_is_refused_and_nothing_changes()
    {
        var (userId, _, access, _, _) = await UserAsync();
        var hashBefore = await PasswordHashAsync(userId);

        var error = await Json.ErrorAsync(await Change(access, ApiFixture.Password, new string('p', 129)),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("newPassword", Json.FieldNames(error));
        Assert.Equal(hashBefore, await PasswordHashAsync(userId));
        Assert.Equal(2, await ActiveRefreshTokensAsync(userId));
    }
}
