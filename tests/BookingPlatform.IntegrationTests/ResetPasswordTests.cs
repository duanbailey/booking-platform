using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T7a: POST /v1/auth/reset-password. Story "User can recover a forgotten password": a link
/// works within 1 hour, once, and only the newest one works; it sets the password, revokes
/// every refresh token, logs the user in and emits PasswordChanged (method Reset), except
/// when it sets an invited account's first password, which accepts the invite instead
/// (specs/05-data-model.md password_resets). Otherwise ResetLinkInvalid and nothing changes.
/// Tests store the token hash directly, standing in for the worker (T26). The clock is frozen.
/// </summary>
public class ResetPasswordTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private const string Path = "/v1/auth/reset-password";
    private const string NewPassword = "a brand new password";

    private Task<HttpResponseMessage> Reset(string token, string password = NewPassword) =>
        fixture.Api.CreateClient().PostJsonAsync(Path, new { token, password });

    private static string NewToken() => Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Guid.NewGuid().ToString("N");

    private Task<HttpResponseMessage> Login(string email, string password) =>
        fixture.Api.CreateClient().PostJsonAsync("/v1/auth/login", new { email, password });

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

    /// <summary>A registered user (with a password and one login) and one sent, valid recovery link.</summary>
    private async Task<(Guid UserId, string Email, string Token, Guid ResetId, string RefreshToken)> UserWithLinkAsync()
    {
        var email = ApiFixture.NewEmail();
        var registered = await fixture.RegisterAsync(email);
        var userId = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM users WHERE email = @email", new { email }))!);
        var token = NewToken();
        var resetId = await fixture.Sql.InsertPasswordResetAsync(userId, token, expiresAt: fixture.Now.AddHours(1),
            createdAt: fixture.Now.AddMinutes(-1));
        return (userId, email, token, resetId, registered.GetProperty("refreshToken").GetString()!);
    }

    private async Task<(string? PasswordHash, long ActiveRefreshTokens)> StateAsync(Guid userId) =>
        (await PasswordHashAsync(userId), await ActiveRefreshTokensAsync(userId));

    /// <summary>Sends the request and checks it is refused with ResetLinkInvalid and changes nothing.</summary>
    private async Task AssertResetLinkInvalidAndNothingChanged(Func<Task<HttpResponseMessage>> request, Guid userId)
    {
        var before = await StateAsync(userId);
        await AssertResetLinkInvalidAndNothingChanged(await request(), userId, before);
    }

    private async Task AssertResetLinkInvalidAndNothingChanged(
        HttpResponseMessage response, Guid userId, (string? PasswordHash, long ActiveRefreshTokens) before)
    {
        await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "ResetLinkInvalid");
        Assert.Equal(before, await StateAsync(userId));
        Assert.Equal(0, await EventCountAsync(userId));
    }

    [Fact]
    public async Task Valid_link_sets_the_password_marks_the_link_used_and_logs_the_user_in()
    {
        var (userId, email, token, resetId, _) = await UserWithLinkAsync();

        var tokens = await Json.ReadAsync(await Reset(token), HttpStatusCode.OK);

        Assert.Equal("Bearer", tokens.GetProperty("tokenType").GetString());
        var me = await Json.ReadAsync(
            await fixture.ClientWithToken(tokens.GetProperty("accessToken").GetString()!).GetAsync("/v1/me"), HttpStatusCode.OK);
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
        await Json.ReadAsync(await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/refresh",
            new { refreshToken = tokens.GetProperty("refreshToken").GetString() }), HttpStatusCode.OK);

        await Json.ReadAsync(await Login(email, NewPassword), HttpStatusCode.OK);
        await Json.ErrorAsync(await Login(email, ApiFixture.Password), HttpStatusCode.Unauthorized, "InvalidCredentials");
        Assert.Equal(fixture.Now, (DateTime)(await fixture.Sql.ScalarAsync(
            "SELECT used_at FROM password_resets WHERE id = @resetId", new { resetId }))!);
    }

    [Fact]
    public async Task Reset_revokes_every_earlier_refresh_token_of_the_user()
    {
        var (userId, email, token, _, registeredRefresh) = await UserWithLinkAsync();
        var secondLogin = await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);

        var tokens = await Json.ReadAsync(await Reset(token), HttpStatusCode.OK);

        foreach (var old in new[] { registeredRefresh, secondLogin.GetProperty("refreshToken").GetString()! })
        {
            await Json.ErrorAsync(await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/refresh", new { refreshToken = old }),
                HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        }
        Assert.Equal(2, await CountAsync(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @userId AND revoked_at = @now AND token_hash <> @newHash",
            new { userId, now = fixture.Now, newHash = Sql.TokenHash(tokens.GetProperty("refreshToken").GetString()!) }));
        Assert.Equal(1, await ActiveRefreshTokensAsync(userId));
    }

    [Fact]
    public async Task Reset_emits_PasswordChanged_with_method_Reset()
    {
        var (userId, _, token, _, _) = await UserWithLinkAsync();

        await Json.ReadAsync(await Reset(token), HttpStatusCode.OK);

        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE event_type = 'PasswordChanged' AND message_group_id = @id",
            new { id = userId.ToString() }))!;
        EventSchemas.AssertValid(payload);
        var data = JsonDocument.Parse(payload).RootElement.GetProperty("data");
        Assert.Equal(userId, data.GetProperty("userId").GetGuid());
        Assert.Equal("Reset", data.GetProperty("method").GetString());
    }

    [Fact]
    public async Task Used_link_is_refused()
    {
        var (userId, email, token, _, _) = await UserWithLinkAsync();
        await Json.ReadAsync(await Reset(token), HttpStatusCode.OK);
        await fixture.Sql.ExecuteAsync("DELETE FROM outbox WHERE message_group_id = @id", new { id = userId.ToString() });

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(token, "some other password"), userId);
        await Json.ReadAsync(await Login(email, NewPassword), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Link_expiring_exactly_now_is_refused()
    {
        var (userId, _, _, _, _) = await UserWithLinkAsync();
        var token = NewToken();
        await fixture.Sql.InsertPasswordResetAsync(userId, token, expiresAt: fixture.Now, createdAt: fixture.Now);

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(token), userId);
    }

    [Fact]
    public async Task Link_after_its_expiry_is_refused()
    {
        var (userId, _, _, _, _) = await UserWithLinkAsync();
        var token = NewToken();
        await fixture.Sql.InsertPasswordResetAsync(userId, token, expiresAt: fixture.Now.AddMinutes(-1), createdAt: fixture.Now);

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(token), userId);
    }

    [Fact]
    public async Task Link_one_second_before_its_expiry_still_works()
    {
        var (userId, _, _, _, _) = await UserWithLinkAsync();
        var token = NewToken();
        await fixture.Sql.InsertPasswordResetAsync(userId, token, expiresAt: fixture.Now.AddSeconds(1), createdAt: fixture.Now);

        await Json.ReadAsync(await Reset(token), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_token_is_refused()
    {
        var (userId, _, _, _, _) = await UserWithLinkAsync();

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(NewToken()), userId);
    }

    [Fact]
    public async Task Token_hash_itself_is_not_accepted_as_a_token()
    {
        var (userId, _, token, _, _) = await UserWithLinkAsync();

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(Sql.TokenHash(token)), userId);
    }

    [Fact]
    public async Task Invite_token_is_not_accepted_as_a_recovery_token()
    {
        var (userId, _, _, _, _) = await UserWithLinkAsync();
        var inviteToken = NewToken();
        await fixture.Sql.InsertInviteAsync(userId, inviteToken, expiresAt: fixture.Now.AddDays(7));

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(inviteToken), userId);
    }

    [Fact]
    public async Task Older_link_stops_working_once_a_newer_row_exists_even_before_it_is_sent()
    {
        var (userId, _, olderToken, _, _) = await UserWithLinkAsync();
        await fixture.Sql.InsertPasswordResetAsync(userId, token: null, createdAt: fixture.Now);

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(olderToken), userId);
    }

    [Fact]
    public async Task Older_link_is_refused_while_a_newer_sent_link_is_still_valid()
    {
        var (userId, _, olderToken, _, _) = await UserWithLinkAsync();
        await fixture.Sql.InsertPasswordResetAsync(userId, NewToken(), expiresAt: fixture.Now.AddHours(1), createdAt: fixture.Now);

        await AssertResetLinkInvalidAndNothingChanged(() => Reset(olderToken), userId);
    }

    [Fact]
    public async Task Newest_link_works_when_older_ones_exist()
    {
        var (userId, _, olderToken, _, _) = await UserWithLinkAsync();
        var newerToken = NewToken();
        await fixture.Sql.InsertPasswordResetAsync(userId, newerToken, expiresAt: fixture.Now.AddHours(1), createdAt: fixture.Now);

        await Json.ReadAsync(await Reset(newerToken), HttpStatusCode.OK);
        await Json.ErrorAsync(await Reset(olderToken), HttpStatusCode.BadRequest, "ResetLinkInvalid");
    }

    [Fact]
    public async Task Links_created_in_the_same_instant_are_ordered_by_the_greater_id()
    {
        var (userId, _, _, _, _) = await UserWithLinkAsync();
        var (lowerToken, higherToken) = (NewToken(), NewToken());
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var higherId = Guid.Parse("ffffffff-0000-0000-0000-000000000002");
        // Insert the newer-by-id row first, so insertion order can't be what decides.
        await fixture.Sql.InsertPasswordResetAsync(userId, higherToken, expiresAt: fixture.Now.AddHours(1),
            createdAt: fixture.Now, id: higherId);
        await fixture.Sql.InsertPasswordResetAsync(userId, lowerToken, expiresAt: fixture.Now.AddHours(1),
            createdAt: fixture.Now, id: lowerId);

        await Json.ErrorAsync(await Reset(lowerToken), HttpStatusCode.BadRequest, "ResetLinkInvalid");
        await Json.ReadAsync(await Reset(higherToken), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Link_for_an_account_without_a_password_accepts_its_invites_and_emits_no_event()
    {
        // Story scenario "an account was created with an invite and has no password yet";
        // no PasswordChanged, since there was no password before (decided 2026-11-19).
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);
        var inviteToken = NewToken();
        await fixture.Sql.InsertInviteAsync(userId, inviteToken, expiresAt: fixture.Now.AddDays(7));
        await fixture.Sql.InsertInviteAsync(userId, token: null, createdAt: fixture.Now);
        var token = NewToken();
        await fixture.Sql.InsertPasswordResetAsync(userId, token, expiresAt: fixture.Now.AddHours(1), createdAt: fixture.Now);

        await Json.ReadAsync(await Reset(token), HttpStatusCode.OK);

        await Json.ReadAsync(await Login(email, NewPassword), HttpStatusCode.OK);
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM user_invites WHERE user_id = @userId AND (used_at IS NULL OR used_at <> @now)",
            new { userId, now = fixture.Now }));
        await Json.ErrorAsync(await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/accept-invite",
            new { token = inviteToken, password = "yet another password" }), HttpStatusCode.BadRequest, "InviteInvalid");
        Assert.Equal(0, await EventCountAsync(userId));
    }

    [Fact]
    public async Task Two_simultaneous_resets_with_one_link_give_one_success_and_one_ResetLinkInvalid()
    {
        var (userId, email, token, _, _) = await UserWithLinkAsync();

        var responses = await Task.WhenAll(Reset(token, "first password!"), Reset(token, "second password!"));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.BadRequest], responses.Select(r => r.StatusCode).Order());
        await Json.ErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.BadRequest),
            HttpStatusCode.BadRequest, "ResetLinkInvalid");
        Assert.Equal(1, await ActiveRefreshTokensAsync(userId));
        Assert.Equal(1, await EventCountAsync(userId));
        var logins = await Task.WhenAll(Login(email, "first password!"), Login(email, "second password!"));
        Assert.Single(logins, r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_waits_for_the_user_row_lock_and_then_sees_a_newer_row_added_meanwhile()
    {
        // A forgot-password holding the lock commits a newer row: the waiting reset must refuse the old link.
        var (userId, _, token, _, _) = await UserWithLinkAsync();
        var before = await StateAsync(userId);
        Task<HttpResponseMessage> reset;
        await using (var held = await fixture.Sql.LockUserRowAsync(userId))
        {
            reset = Reset(token);
            await fixture.Sql.WaitForLockWaitAsync("users");
            Assert.False(reset.IsCompleted);
            await held.ExecuteAsync(
                "INSERT INTO password_resets (id, user_id, token_hash, expires_at, used_at, created_at) " +
                "VALUES (@id, @userId, NULL, NULL, NULL, @createdAt)",
                new { id = Guid.NewGuid(), userId, createdAt = fixture.Now });
            await held.CommitAsync();
        }

        await AssertResetLinkInvalidAndNothingChanged(await reset, userId, before);
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
        var (userId, _, token, _, _) = await UserWithLinkAsync();
        var hashBefore = await PasswordHashAsync(userId);

        var error = await Json.ErrorAsync(await Reset(token, new string('p', 129)), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("password", Json.FieldNames(error));
        Assert.Equal(hashBefore, await PasswordHashAsync(userId));
        await Json.ReadAsync(await Reset(token), HttpStatusCode.OK);
    }
}
