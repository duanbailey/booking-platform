using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T7a: POST /v1/auth/forgot-password. Story "User can recover a forgotten password": the
/// same 202 whether or not the account exists, and at most one recovery row (and
/// PasswordResetRequested event) per account per 5 minutes (specs/05-data-model.md
/// password_resets, specs/07-nfr.md). The API's clock is frozen.
/// </summary>
public class ForgotPasswordTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private const string Path = "/v1/auth/forgot-password";

    private Task<HttpResponseMessage> Forgot(string email) =>
        fixture.Api.CreateClient().PostJsonAsync(Path, new { email });

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<long> ResetCountAsync(Guid userId) =>
        CountAsync("SELECT COUNT(*) FROM password_resets WHERE user_id = @userId", new { userId });

    private Task<long> EventCountAsync(Guid userId) =>
        CountAsync("SELECT COUNT(*) FROM outbox WHERE event_type = 'PasswordResetRequested' AND message_group_id = @id",
            new { id = userId.ToString() });

    private static async Task AssertAccepted(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Existing_account_gets_an_unsent_recovery_row_and_a_PasswordResetRequested_event()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");

        await AssertAccepted(await Forgot(email));

        // The row is left for the worker (T26): no token, no expiry, not used.
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM password_resets WHERE user_id = @userId AND token_hash IS NULL " +
            "AND expires_at IS NULL AND used_at IS NULL AND created_at = @now",
            new { userId, now = fixture.Now }));
        var resetId = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM password_resets WHERE user_id = @userId", new { userId }))!);

        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE event_type = 'PasswordResetRequested' AND message_group_id = @id",
            new { id = userId.ToString() }))!;
        EventSchemas.AssertValid(payload);
        var data = JsonDocument.Parse(payload).RootElement.GetProperty("data");
        Assert.Equal(userId, data.GetProperty("userId").GetGuid());
        Assert.Equal(resetId, data.GetProperty("passwordResetId").GetGuid());
    }

    [Fact]
    public async Task Unknown_email_gets_the_same_reply_and_nothing_is_stored()
    {
        var known = ApiFixture.NewEmail();
        await fixture.Sql.InsertUserAsync(known, passwordHash: "some-hash");
        var unknown = ApiFixture.NewEmail();
        var outboxBefore = await CountAsync("SELECT COUNT(*) FROM outbox", new { });
        var resetsBefore = await CountAsync("SELECT COUNT(*) FROM password_resets", new { });

        var unknownResponse = await Forgot(unknown);

        await AssertAccepted(unknownResponse);
        Assert.Equal(outboxBefore, await CountAsync("SELECT COUNT(*) FROM outbox", new { }));
        Assert.Equal(resetsBefore, await CountAsync("SELECT COUNT(*) FROM password_resets", new { }));

        // Same status, body and content headers as for an existing account.
        var knownResponse = await Forgot(known);
        await AssertAccepted(knownResponse);
        Assert.Equal(knownResponse.Content.Headers.ContentType, unknownResponse.Content.Headers.ContentType);
        Assert.Equal(knownResponse.Content.Headers.ContentLength, unknownResponse.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Email_is_matched_ignoring_case_and_surrounding_spaces()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");

        await AssertAccepted(await Forgot($" {email.ToUpperInvariant()} "));

        Assert.Equal(1, await ResetCountAsync(userId));
    }

    [Fact]
    public async Task Account_without_a_password_also_gets_a_recovery_row()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);

        await AssertAccepted(await Forgot(email));

        Assert.Equal(1, await ResetCountAsync(userId));
        Assert.Equal(1, await EventCountAsync(userId));
    }

    [Fact]
    public async Task Request_within_5_minutes_of_the_newest_row_stores_nothing_and_gets_the_same_reply()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");
        await fixture.Sql.InsertPasswordResetAsync(userId, createdAt: fixture.Now.AddMinutes(-5).AddSeconds(1));

        await AssertAccepted(await Forgot(email));

        Assert.Equal(1, await ResetCountAsync(userId));
        Assert.Equal(0, await EventCountAsync(userId));
    }

    [Fact]
    public async Task Cooldown_counts_the_newest_row_even_if_it_was_already_used()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");
        await fixture.Sql.InsertPasswordResetAsync(userId, token: Guid.NewGuid().ToString(),
            expiresAt: fixture.Now.AddMinutes(58), createdAt: fixture.Now.AddMinutes(-2), usedAt: fixture.Now.AddMinutes(-1));

        await AssertAccepted(await Forgot(email));

        Assert.Equal(1, await ResetCountAsync(userId));
    }

    [Fact]
    public async Task Request_exactly_5_minutes_after_the_newest_row_creates_a_new_one()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");
        await fixture.Sql.InsertPasswordResetAsync(userId, createdAt: fixture.Now.AddMinutes(-10));
        await fixture.Sql.InsertPasswordResetAsync(userId, createdAt: fixture.Now.AddMinutes(-5));

        await AssertAccepted(await Forgot(email));

        Assert.Equal(3, await ResetCountAsync(userId));
        Assert.Equal(1, await EventCountAsync(userId));
    }

    [Fact]
    public async Task Two_simultaneous_requests_store_one_row()
    {
        // The user row lock makes the second request see the first one's row.
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");

        var responses = await Task.WhenAll(Forgot(email), Forgot(email), Forgot(email));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await ResetCountAsync(userId));
        Assert.Equal(1, await EventCountAsync(userId));
    }

    [Fact]
    public async Task Request_waits_for_the_user_row_lock_and_then_sees_a_row_added_meanwhile()
    {
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");
        Task<HttpResponseMessage> forgot;
        await using (var held = await fixture.Sql.LockUserRowAsync(userId))
        {
            forgot = Forgot(email);
            await fixture.Sql.WaitForLockWaitAsync("users");
            Assert.False(forgot.IsCompleted);
            await held.ExecuteAsync(
                "INSERT INTO password_resets (id, user_id, token_hash, expires_at, used_at, created_at) " +
                "VALUES (@id, @userId, NULL, NULL, NULL, @createdAt)",
                new { id = Guid.NewGuid(), userId, createdAt = fixture.Now });
            await held.CommitAsync();
        }

        await AssertAccepted(await forgot);
        Assert.Equal(1, await ResetCountAsync(userId));
        Assert.Equal(0, await EventCountAsync(userId));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"email":""}""")]
    [InlineData("""{"email":"not-an-email"}""")]
    public async Task Invalid_email_returns_400_ValidationFailed(string json)
    {
        var error = await Json.ErrorAsync(await fixture.Api.CreateClient().PostAsync(Path, Json.Body(json)),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("email", Json.FieldNames(error));
    }
}
