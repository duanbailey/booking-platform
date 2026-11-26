using System.Net;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T7: POST /v1/auth/accept-invite. Story "Invited user sets their password": a link works
/// within 7 days, once, and only the newest one works; otherwise InviteInvalid and nothing
/// changes (specs/05-data-model.md user_invites). Tests store the token hash directly,
/// standing in for the worker (T26). The API's clock is frozen (<see cref="FrozenClockApiFixture"/>).
/// </summary>
public class AcceptInviteTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private const string Path = "/v1/auth/accept-invite";
    private const string NewPassword = "a brand new password";

    private Task<HttpResponseMessage> Accept(string token, string password = NewPassword) =>
        fixture.Api.CreateClient().PostJsonAsync(Path, new { token, password });

    private static string NewToken() => Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Guid.NewGuid().ToString("N");

    /// <summary>An invited user (no password) with one sent, valid invite.</summary>
    private async Task<(Guid UserId, string Email, string Token, Guid InviteId)> InvitedUserAsync()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);
        var token = NewToken();
        var inviteId = await fixture.Sql.InsertInviteAsync(userId, token, expiresAt: fixture.Now.AddDays(7));
        return (userId, email, token, inviteId);
    }

    private async Task<string?> PasswordHashAsync(Guid userId) =>
        await fixture.Sql.ScalarAsync("SELECT password_hash FROM users WHERE id = @userId", new { userId }) is string hash
            ? hash : null;

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<HttpResponseMessage> Login(string email, string password) =>
        fixture.Api.CreateClient().PostJsonAsync("/v1/auth/login", new { email, password });

    private async Task AssertInviteInvalidAndNothingChanged(HttpResponseMessage response, Guid userId, string? passwordHashBefore = null)
    {
        await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "InviteInvalid");
        Assert.Equal(passwordHashBefore, await PasswordHashAsync(userId));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId", new { userId }));
    }

    [Fact]
    public async Task Valid_link_sets_the_password_marks_the_invite_used_and_logs_the_user_in()
    {
        var (userId, email, token, inviteId) = await InvitedUserAsync();

        var response = await Accept(token);
        var tokens = await Json.ReadAsync(response, HttpStatusCode.OK);

        // Logged in: an AuthToken whose access token works and whose refresh token is stored.
        Assert.Equal("Bearer", tokens.GetProperty("tokenType").GetString());
        var me = await Json.ReadAsync(
            await fixture.ClientWithToken(tokens.GetProperty("accessToken").GetString()!).GetAsync("/v1/me"), HttpStatusCode.OK);
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId AND token_hash = @hash",
            new { userId, hash = Sql.TokenHash(RefreshCookie.TokenFrom(response)) }));

        // The password is saved: the user can now log in with it.
        await Json.ReadAsync(await Login(email, NewPassword), HttpStatusCode.OK);
        Assert.Equal(fixture.Now, (DateTime)(await fixture.Sql.ScalarAsync(
            "SELECT used_at FROM user_invites WHERE id = @inviteId", new { inviteId }))!);
    }

    [Fact]
    public async Task Used_link_is_refused_with_InviteInvalid_and_nothing_changes()
    {
        var (userId, email, token, _) = await InvitedUserAsync();
        await Json.ReadAsync(await Accept(token), HttpStatusCode.OK);
        var hashAfterFirstUse = await PasswordHashAsync(userId);
        var refreshTokens = await CountAsync("SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId", new { userId });

        await Json.ErrorAsync(await Accept(token, "some other password"), HttpStatusCode.BadRequest, "InviteInvalid");

        Assert.Equal(hashAfterFirstUse, await PasswordHashAsync(userId));
        Assert.Equal(refreshTokens, await CountAsync("SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId", new { userId }));
        await Json.ReadAsync(await Login(email, NewPassword), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Link_expiring_exactly_now_is_refused()
    {
        var userId = await fixture.Sql.InsertUserAsync();
        var token = NewToken();
        await fixture.Sql.InsertInviteAsync(userId, token, expiresAt: fixture.Now);

        await AssertInviteInvalidAndNothingChanged(await Accept(token), userId);
    }

    [Fact]
    public async Task Link_after_its_expiry_is_refused()
    {
        var userId = await fixture.Sql.InsertUserAsync();
        var token = NewToken();
        await fixture.Sql.InsertInviteAsync(userId, token, expiresAt: fixture.Now.AddDays(-1));

        await AssertInviteInvalidAndNothingChanged(await Accept(token), userId);
    }

    [Fact]
    public async Task Link_one_second_before_its_expiry_still_works()
    {
        var userId = await fixture.Sql.InsertUserAsync();
        var token = NewToken();
        await fixture.Sql.InsertInviteAsync(userId, token, expiresAt: fixture.Now.AddSeconds(1));

        await Json.ReadAsync(await Accept(token), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_token_is_refused()
    {
        var (userId, _, _, _) = await InvitedUserAsync();

        await AssertInviteInvalidAndNothingChanged(await Accept(NewToken()), userId);
    }

    [Fact]
    public async Task Token_hash_itself_is_not_accepted_as_a_token()
    {
        var (userId, _, token, _) = await InvitedUserAsync();

        await AssertInviteInvalidAndNothingChanged(await Accept(Sql.TokenHash(token)), userId);
    }

    [Fact]
    public async Task Older_link_stops_working_once_a_newer_invite_exists_even_before_it_is_sent()
    {
        // T7 note: T6 (or a resend) adds a newer row without a token; the older link must stop.
        var (userId, _, olderToken, _) = await InvitedUserAsync();
        await fixture.Sql.InsertInviteAsync(userId, token: null, createdAt: Sql.Now.AddMinutes(1));

        await AssertInviteInvalidAndNothingChanged(await Accept(olderToken), userId);
    }

    [Fact]
    public async Task Newest_link_works_and_marks_every_pending_invite_used()
    {
        var (userId, _, _, olderInvite) = await InvitedUserAsync();
        var newerToken = NewToken();
        var newerInvite = await fixture.Sql.InsertInviteAsync(userId, newerToken, expiresAt: fixture.Now.AddDays(7),
            createdAt: Sql.Now.AddMinutes(1));

        await Json.ReadAsync(await Accept(newerToken), HttpStatusCode.OK);

        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM user_invites WHERE user_id = @userId AND used_at IS NULL", new { userId }));
        Assert.Equal(2, await CountAsync(
            "SELECT COUNT(*) FROM user_invites WHERE id IN (@olderInvite, @newerInvite) AND used_at = @now",
            new { olderInvite, newerInvite, now = fixture.Now }));
    }

    [Fact]
    public async Task Invites_created_in_the_same_instant_are_ordered_by_the_greater_id()
    {
        var userId = await fixture.Sql.InsertUserAsync();
        var (lowerToken, higherToken) = (NewToken(), NewToken());
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        // Insert the newer-by-id row first, so insertion order can't be what decides.
        await fixture.Sql.InsertInviteAsync(userId, higherToken, expiresAt: fixture.Now.AddDays(7), id: higherId);
        await fixture.Sql.InsertInviteAsync(userId, lowerToken, expiresAt: fixture.Now.AddDays(7), id: lowerId);

        await Json.ErrorAsync(await Accept(lowerToken), HttpStatusCode.BadRequest, "InviteInvalid");
        await Json.ReadAsync(await Accept(higherToken), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pending_invite_of_a_user_who_already_has_a_password_is_refused()
    {
        var (userId, _, token, _) = await InvitedUserAsync();
        await fixture.Sql.ExecuteAsync("UPDATE users SET password_hash = 'existing-hash' WHERE id = @userId", new { userId });

        await AssertInviteInvalidAndNothingChanged(await Accept(token), userId, passwordHashBefore: "existing-hash");
    }

    [Fact]
    public async Task Accepting_emits_no_event()
    {
        // specs/06-events.md: PasswordChanged is only for change and recovery.
        var (userId, _, token, _) = await InvitedUserAsync();

        await Json.ReadAsync(await Accept(token), HttpStatusCode.OK);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = userId.ToString() }));
    }

    [Fact]
    public async Task Two_simultaneous_accepts_of_one_link_give_one_success_and_one_InviteInvalid()
    {
        var (userId, email, token, _) = await InvitedUserAsync();

        var responses = await Task.WhenAll(Accept(token, "first password!"), Accept(token, "second password!"));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.BadRequest], responses.Select(r => r.StatusCode).Order());
        await Json.ErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.BadRequest), HttpStatusCode.BadRequest, "InviteInvalid");
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId", new { userId }));
        var logins = await Task.WhenAll(Login(email, "first password!"), Login(email, "second password!"));
        Assert.Single(logins, r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accept_waits_for_the_user_row_lock_and_then_sees_a_newer_invite_added_meanwhile()
    {
        // A resend holding the lock commits a newer invite: the waiting accept must refuse the old link.
        var (userId, _, token, _) = await InvitedUserAsync();
        Task<HttpResponseMessage> accept;
        await using (var held = await fixture.Sql.LockUserRowAsync(userId))
        {
            accept = Accept(token);
            await fixture.Sql.WaitForLockWaitAsync("users");
            Assert.False(accept.IsCompleted);
            await held.ExecuteAsync(
                "INSERT INTO user_invites (id, user_id, token_hash, expires_at, used_at, created_by, created_at) " +
                "VALUES (@id, @userId, NULL, NULL, NULL, @userId, @createdAt)",
                new { id = Guid.NewGuid(), userId, createdAt = Sql.Now.AddMinutes(1) });
            await held.CommitAsync();
        }

        await AssertInviteInvalidAndNothingChanged(await accept, userId);
    }

    [Fact]
    public async Task Accept_waiting_for_the_user_row_lock_succeeds_once_it_is_released()
    {
        var (userId, _, token, _) = await InvitedUserAsync();
        Task<HttpResponseMessage> accept;
        await using (await fixture.Sql.LockUserRowAsync(userId))
        {
            accept = Accept(token);
            await fixture.Sql.WaitForLockWaitAsync("users");
            Assert.False(accept.IsCompleted);
        }

        await Json.ReadAsync(await accept, HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("token", """{"password":"a long enough password"}""")]
    [InlineData("token", """{"token":"","password":"a long enough password"}""")]
    [InlineData("password", """{"token":"abc"}""")]
    [InlineData("password", """{"token":"abc","password":"short"}""")]
    public async Task Invalid_field_returns_400_ValidationFailed_naming_it(string field, string json)
    {
        var error = await Json.ErrorAsync(await fixture.Api.CreateClient().PostAsync(Path, Json.Body(json)),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Fact]
    public async Task Password_longer_than_128_characters_is_refused_and_the_link_still_works()
    {
        var (userId, _, token, _) = await InvitedUserAsync();

        var error = await Json.ErrorAsync(await Accept(token, new string('p', 129)), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("password", Json.FieldNames(error));
        Assert.Null(await PasswordHashAsync(userId));
        await Json.ReadAsync(await Accept(token), HttpStatusCode.OK);
    }
}
