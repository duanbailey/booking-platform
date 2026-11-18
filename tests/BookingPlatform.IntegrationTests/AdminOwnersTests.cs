using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T6: POST /v1/admin/owners. Story "Admin creates an owner and their business"; admin
/// authorization (specs/07-nfr.md); AccessGranted through the outbox (specs/06-events.md).
/// </summary>
public class AdminOwnersTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const string Path = "/v1/admin/owners";

    private static object Request(string email, string ownerName = "Olive Owner", string businessName = "Cut & Dry",
        string timeZone = "America/New_York") =>
        new { ownerName, ownerEmail = email, businessName, businessTimeZone = timeZone };

    private async Task<(HttpClient Client, Guid AdminId)> AdminAsync()
    {
        var admin = await fixture.Sql.InsertAdminUserAsync();
        return (fixture.Api.ClientFor(admin), admin);
    }

    private async Task<Guid?> UserIdAsync(string email) =>
        await TextAsync("SELECT id FROM users WHERE email = @email", new { email }) is { } id ? Guid.Parse(id) : null;

    /// <summary>A scalar as text (char(36) IDs come back as Guid), or null for no row or NULL.</summary>
    private async Task<string?> TextAsync(string sql, object parameters) =>
        await fixture.Sql.ScalarAsync(sql, parameters) is { } value and not DBNull ? Convert.ToString(value) : null;

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    /// <summary>The AccessGranted payloads written for a user, oldest first.</summary>
    private async Task<List<JsonElement>> AccessGrantedAsync(Guid userId)
    {
        var events = new List<JsonElement>();
        var count = await CountAsync(
            "SELECT COUNT(*) FROM outbox WHERE event_type = 'AccessGranted' AND message_group_id = @id", new { id = userId.ToString() });
        for (var i = 0; i < count; i++)
        {
            var payload = (await TextAsync(
                "SELECT payload FROM outbox WHERE event_type = 'AccessGranted' AND message_group_id = @id " +
                "ORDER BY created_at, id LIMIT 1 OFFSET " + i, new { id = userId.ToString() }))!;
            EventSchemas.AssertValid(payload);
            events.Add(JsonDocument.Parse(payload).RootElement);
        }
        return events;
    }

    [Fact]
    public async Task New_email_creates_an_account_without_password_a_business_with_defaults_and_an_invite()
    {
        var (admin, adminId) = await AdminAsync();
        var email = ApiFixture.NewEmail();

        var response = await admin.PostJsonAsync(Path, Request(email, ownerName: "  Olive Owner  "));
        var body = await Json.ReadAsync(response, HttpStatusCode.Created);

        // Business with default settings (specs/05-data-model.md; R3, R4, R8).
        var businessId = Guid.Parse(body.GetProperty("id").GetString()!);
        Assert.Equal("Cut & Dry", body.GetProperty("name").GetString());
        Assert.Equal("America/New_York", body.GetProperty("timeZone").GetString());
        Assert.Equal(120, body.GetProperty("minNoticeMinutes").GetInt32());
        Assert.Equal(24, body.GetProperty("cancellationWindowHours").GetInt32());
        Assert.Equal(21, body.GetProperty("maxAdvanceDays").GetInt32());
        Assert.Equal(30, body.GetProperty("slotStepMinutes").GetInt32());
        Assert.Equal($"/v1/businesses/{businessId}", response.Headers.Location?.OriginalString);

        // Account without a password, owning the business; time zone from the business.
        var userId = (await UserIdAsync(email))!.Value;
        Assert.Null(await TextAsync("SELECT password_hash FROM users WHERE id = @userId", new { userId }));
        Assert.Equal("Olive Owner", await TextAsync("SELECT name FROM users WHERE id = @userId", new { userId }));
        Assert.Equal("America/New_York", await TextAsync("SELECT time_zone FROM users WHERE id = @userId", new { userId }));
        Assert.Equal(userId.ToString(), await TextAsync(
            "SELECT owner_user_id FROM businesses WHERE id = @businessId", new { businessId }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM staff WHERE user_id = @userId", new { userId }));

        // Invite row without a token (the worker makes it), created by the admin.
        var inviteId = (await TextAsync(
            "SELECT id FROM user_invites WHERE user_id = @userId AND created_by = @adminId " +
            "AND token_hash IS NULL AND expires_at IS NULL AND used_at IS NULL", new { userId, adminId }));
        Assert.NotNull(inviteId);

        var granted = Assert.Single(await AccessGrantedAsync(userId));
        var data = granted.GetProperty("data");
        Assert.Equal(userId.ToString(), data.GetProperty("userId").GetString());
        Assert.Equal(businessId.ToString(), data.GetProperty("businessId").GetString());
        Assert.Equal("Owner", data.GetProperty("role").GetString());
        Assert.Equal(inviteId, data.GetProperty("inviteId").GetString());
        Assert.Equal(adminId.ToString(), data.GetProperty("grantedByUserId").GetString());
    }

    [Fact]
    public async Task Existing_account_with_a_password_becomes_owner_and_gets_a_notice_not_an_invite()
    {
        var (admin, adminId) = await AdminAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "not-a-real-hash");

        var body = await Json.ReadAsync(await admin.PostJsonAsync(Path, Request(email, ownerName: "Ignored Name")),
            HttpStatusCode.Created);

        Assert.Equal(userId.ToString(), await TextAsync(
            "SELECT owner_user_id FROM businesses WHERE id = @id", new { id = body.GetProperty("id").GetString() }));
        Assert.Equal("Test User", await TextAsync("SELECT name FROM users WHERE id = @userId", new { userId }));
        Assert.Equal("Europe/London", await TextAsync("SELECT time_zone FROM users WHERE id = @userId", new { userId }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId }));

        var data = Assert.Single(await AccessGrantedAsync(userId)).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("inviteId").ValueKind);
        Assert.Equal(adminId.ToString(), data.GetProperty("grantedByUserId").GetString());
    }

    [Fact]
    public async Task Existing_account_without_a_password_becomes_owner_and_gets_a_new_invite()
    {
        var (admin, adminId) = await AdminAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email); // invited elsewhere, never accepted
        var olderInvite = Guid.NewGuid();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO user_invites (id, user_id, token_hash, expires_at, used_at, created_by, created_at) " +
            "VALUES (@olderInvite, @userId, 'older-hash', @expires, NULL, @userId, @created)",
            new { olderInvite, userId, expires = Sql.Now.AddDays(7), created = Sql.Now });

        await Json.ReadAsync(await admin.PostJsonAsync(Path, Request(email)), HttpStatusCode.Created);

        var newInvite = (await TextAsync(
            "SELECT id FROM user_invites WHERE user_id = @userId AND id <> @olderInvite AND created_by = @adminId " +
            "AND token_hash IS NULL", new { userId, olderInvite, adminId }));
        Assert.NotNull(newInvite);
        var data = Assert.Single(await AccessGrantedAsync(userId)).GetProperty("data");
        Assert.Equal(newInvite, data.GetProperty("inviteId").GetString());
    }

    [Fact]
    public async Task Account_that_already_owns_a_business_is_refused_with_409_AlreadyOwner_and_nothing_changes()
    {
        var (admin, _) = await AdminAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);
        await fixture.Sql.InsertBusinessAsync(userId);
        var businessesBefore = await CountAsync("SELECT COUNT(*) FROM businesses", new { });

        await Json.ErrorAsync(await admin.PostJsonAsync(Path, Request(email)), HttpStatusCode.Conflict, "AlreadyOwner");

        Assert.Equal(businessesBefore, await CountAsync("SELECT COUNT(*) FROM businesses", new { }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId }));
        Assert.Empty(await AccessGrantedAsync(userId));
    }

    [Fact]
    public async Task Email_matches_an_existing_account_ignoring_case_and_surrounding_spaces()
    {
        var (admin, _) = await AdminAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "not-a-real-hash");

        var body = await Json.ReadAsync(await admin.PostJsonAsync(Path, Request($"  {email.ToUpperInvariant()} ")),
            HttpStatusCode.Created);

        Assert.Equal(userId.ToString(), await TextAsync(
            "SELECT owner_user_id FROM businesses WHERE id = @id", new { id = body.GetProperty("id").GetString() }));
    }

    [Fact]
    public async Task New_account_email_and_business_name_are_stored_trimmed()
    {
        var (admin, _) = await AdminAsync();
        var email = ApiFixture.NewEmail();

        var body = await Json.ReadAsync(await admin.PostJsonAsync(Path, Request($"  {email}  ", businessName: "  Cut & Dry  ")),
            HttpStatusCode.Created);

        Assert.Equal("Cut & Dry", body.GetProperty("name").GetString());
        Assert.NotNull(await UserIdAsync(email));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM users WHERE email LIKE @padded", new { padded = " %" }));
    }

    [Fact]
    public async Task Non_admin_gets_403_Forbidden_and_nothing_is_created()
    {
        var user = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        var email = ApiFixture.NewEmail();

        await Json.ErrorAsync(await fixture.Api.ClientFor(user).PostJsonAsync(Path, Request(email)),
            HttpStatusCode.Forbidden, "Forbidden");

        Assert.Null(await UserIdAsync(email));
    }

    [Fact]
    public async Task Owner_or_staff_of_a_business_is_not_an_admin()
    {
        // specs/07-nfr.md: admin is only a platform_admins row.
        var owner = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        await fixture.Sql.InsertBusinessAsync(owner);

        await Json.ErrorAsync(await fixture.Api.ClientFor(owner).PostJsonAsync(Path, Request(ApiFixture.NewEmail())),
            HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Without_a_token_the_request_gets_401()
    {
        await Json.ErrorAsync(await fixture.Api.CreateClient().PostJsonAsync(Path, Request(ApiFixture.NewEmail())),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Theory]
    [InlineData("ownerName", """{"ownerEmail":"a@example.test","businessName":"B","businessTimeZone":"Europe/London"}""")]
    [InlineData("ownerName", """{"ownerName":"   ","ownerEmail":"a@example.test","businessName":"B","businessTimeZone":"Europe/London"}""")]
    [InlineData("ownerEmail", """{"ownerName":"A","businessName":"B","businessTimeZone":"Europe/London"}""")]
    [InlineData("ownerEmail", """{"ownerName":"A","ownerEmail":"not-an-email","businessName":"B","businessTimeZone":"Europe/London"}""")]
    [InlineData("businessName", """{"ownerName":"A","ownerEmail":"a@example.test","businessTimeZone":"Europe/London"}""")]
    [InlineData("businessName", """{"ownerName":"A","ownerEmail":"a@example.test","businessName":"   ","businessTimeZone":"Europe/London"}""")]
    [InlineData("businessTimeZone", """{"ownerName":"A","ownerEmail":"a@example.test","businessName":"B"}""")]
    [InlineData("businessTimeZone", """{"ownerName":"A","ownerEmail":"a@example.test","businessName":"B","businessTimeZone":"europe/london"}""")]
    public async Task Invalid_field_returns_400_ValidationFailed_naming_it(string field, string json)
    {
        var (admin, _) = await AdminAsync();

        var error = await Json.ErrorAsync(await admin.PostAsync(Path, Json.Body(json)), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Fact]
    public async Task Business_name_longer_than_200_characters_is_refused()
    {
        var (admin, _) = await AdminAsync();

        var error = await Json.ErrorAsync(await admin.PostJsonAsync(Path,
            Request(ApiFixture.NewEmail(), businessName: new string('b', 201))), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("businessName", Json.FieldNames(error));
    }

    [Fact]
    public async Task Two_simultaneous_creates_for_a_new_email_give_one_owner_and_one_409()
    {
        var (admin, adminId) = await AdminAsync();
        var email = ApiFixture.NewEmail();

        var responses = await Task.WhenAll(
            admin.PostJsonAsync(Path, Request(email, businessName: "First")),
            fixture.Api.ClientFor(adminId).PostJsonAsync(Path, Request(email, businessName: "Second")));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        await Json.ErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "AlreadyOwner");
        var userId = (await UserIdAsync(email))!.Value;
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM businesses WHERE owner_user_id = @userId", new { userId }));
        Assert.Single(await AccessGrantedAsync(userId));
    }

    [Fact]
    public async Task Two_simultaneous_creates_for_an_existing_account_give_one_owner_and_one_409()
    {
        var (admin, _) = await AdminAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "not-a-real-hash");

        var responses = await Task.WhenAll(
            admin.PostJsonAsync(Path, Request(email, businessName: "First")),
            admin.PostJsonAsync(Path, Request(email, businessName: "Second")));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM businesses WHERE owner_user_id = @userId", new { userId }));
        Assert.Single(await AccessGrantedAsync(userId));
    }
}
