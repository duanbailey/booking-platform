using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T4: POST /v1/auth/register. Story "Customer can sign up": the account is created and
/// the visitor is logged in; an email that already belongs to an account is refused.
/// </summary>
public class RegisterTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private Task<HttpResponseMessage> Register(object body) =>
        fixture.Api.CreateClient().PostJsonAsync("/v1/auth/register", body);

    private static object Valid(string email) =>
        new { name = "Ann Example", email, password = ApiFixture.Password, timeZone = "Europe/London" };

    [Fact]
    public async Task Sign_up_creates_the_account_and_logs_the_visitor_in()
    {
        var email = ApiFixture.NewEmail();

        var tokens = await Json.ReadAsync(await Register(Valid(email)), HttpStatusCode.Created);

        Assert.Equal("Bearer", tokens.GetProperty("tokenType").GetString());
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refreshToken").GetString()));
        Assert.True(tokens.GetProperty("refreshTokenExpiresAt").GetDateTime() > tokens.GetProperty("expiresAt").GetDateTime());

        // The access token works straight away and belongs to a plain customer account.
        var me = await Json.ReadAsync(
            await fixture.ClientWithToken(tokens.GetProperty("accessToken").GetString()!).GetAsync("/v1/me"),
            HttpStatusCode.OK);
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal("Ann Example", me.GetProperty("name").GetString());
        Assert.Equal("Europe/London", me.GetProperty("timeZone").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("ownedBusinessId").ValueKind);
        Assert.Empty(me.GetProperty("staff").EnumerateArray());
    }

    [Fact]
    public async Task Password_is_stored_only_as_a_hash()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);

        var hash = (string?)await fixture.Sql.ScalarAsync("SELECT password_hash FROM users WHERE email = @email", new { email });

        Assert.NotNull(hash);
        Assert.DoesNotContain(ApiFixture.Password, hash);
        Assert.True(hash.Length > 40);
    }

    [Fact]
    public async Task Email_is_stored_trimmed()
    {
        var email = ApiFixture.NewEmail();

        await Json.ReadAsync(await Register(Valid($"  {email} ")), HttpStatusCode.Created);

        Assert.Equal(1L, Convert.ToInt64(await fixture.Sql.ScalarAsync(
            "SELECT COUNT(*) FROM users WHERE email = @email AND email = BINARY @email", new { email })));
    }

    [Fact]
    public async Task Email_that_belongs_to_an_account_is_refused_with_409_EmailTaken()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);

        await Json.ErrorAsync(await Register(Valid(email)), HttpStatusCode.Conflict, "EmailTaken");
    }

    [Fact]
    public async Task Email_comparison_ignores_case_and_surrounding_spaces()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);

        await Json.ErrorAsync(await Register(Valid($" {email.ToUpperInvariant()} ")), HttpStatusCode.Conflict, "EmailTaken");
    }

    [Fact]
    public async Task Email_differing_only_in_accents_is_a_different_account()
    {
        // specs/05-data-model.md: emails are compared ignoring case but not accents.
        var suffix = Guid.NewGuid().ToString("N");
        await fixture.RegisterAsync($"ann-{suffix}@example.test");

        await Json.ReadAsync(await Register(Valid($"ánn-{suffix}@example.test")), HttpStatusCode.Created);
        await Json.ErrorAsync(await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/login",
            new { email = $"ánn-{suffix}@example.test", password = "wrong for this account" }), HttpStatusCode.Unauthorized, "InvalidCredentials");
    }

    [Fact]
    public async Task Email_of_an_invited_account_without_a_password_is_taken_too()
    {
        var email = ApiFixture.NewEmail();
        await fixture.Sql.InsertUserAsync(email); // password_hash NULL, as for an invite not yet accepted

        await Json.ErrorAsync(await Register(Valid(email)), HttpStatusCode.Conflict, "EmailTaken");
    }

    [Fact]
    public async Task Two_simultaneous_sign_ups_with_one_email_give_one_account()
    {
        var email = ApiFixture.NewEmail();

        var responses = await Task.WhenAll(Register(Valid(email)), Register(Valid(email)));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1L, Convert.ToInt64(await fixture.Sql.ScalarAsync(
            "SELECT COUNT(*) FROM users WHERE email = @email", new { email })));
    }

    [Theory]
    [InlineData("email", """{"name":"Ann","email":"not-an-email","password":"12345678","timeZone":"Europe/London"}""")]
    [InlineData("password", """{"name":"Ann","email":"a@example.test","password":"1234567","timeZone":"Europe/London"}""")]
    [InlineData("name", """{"name":"","email":"a@example.test","password":"12345678","timeZone":"Europe/London"}""")]
    [InlineData("timeZone", """{"name":"Ann","email":"a@example.test","password":"12345678","timeZone":"Mars/Olympus_Mons"}""")]
    [InlineData("timeZone", """{"name":"Ann","email":"a@example.test","password":"12345678","timeZone":"GMT Standard Time"}""")]
    [InlineData("timeZone", """{"name":"Ann","email":"a@example.test","password":"12345678","timeZone":"europe/london"}""")]
    [InlineData("timeZone", """{"name":"Ann","email":"a@example.test","password":"12345678"}""")]
    public async Task Invalid_field_returns_400_ValidationFailed_naming_it(string field, string json)
    {
        var response = await fixture.Api.CreateClient().PostAsync("/v1/auth/register", Json.Body(json));

        var error = await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains(field, Json.FieldNames(error));
    }

    [Theory]
    [InlineData("a b@example.test")]   // space inside
    [InlineData("a@b@example.test")]   // two @
    [InlineData("@example.test")]      // nothing before @
    [InlineData("ann@")]               // nothing after @
    [InlineData("ann\t@example.test")] // tab inside
    public async Task Malformed_email_is_refused(string email)
    {
        var error = await Json.ErrorAsync(await Register(Valid(email)), HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("email", Json.FieldNames(error));
    }

    [Fact]
    public async Task Email_length_is_checked_after_trimming()
    {
        var email = new string('a', 254 - "@example.test".Length) + "@example.test"; // exactly 254
        await Json.ReadAsync(await Register(Valid($"  {email}  ")), HttpStatusCode.Created);

        var tooLong = "a" + email;
        var error = await Json.ErrorAsync(await Register(Valid(tooLong)), HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("email", Json.FieldNames(error));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Name_of_only_whitespace_is_refused(string name)
    {
        var body = new { name, email = ApiFixture.NewEmail(), password = ApiFixture.Password, timeZone = "UTC" };

        var error = await Json.ErrorAsync(await Register(body), HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("name", Json.FieldNames(error));
    }

    [Fact]
    public async Task Name_is_stored_trimmed_and_its_length_checked_after_trimming()
    {
        var name = new string('n', 200);
        var login = await fixture.RegisterAsync(name: $"  {name}  ");

        var me = await Json.ReadAsync(
            await fixture.ClientWithToken(login.GetProperty("accessToken").GetString()!).GetAsync("/v1/me"), HttpStatusCode.OK);
        Assert.Equal(name, me.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Password_longer_than_128_characters_is_refused()
    {
        var body = new { name = "Ann", email = ApiFixture.NewEmail(), password = new string('p', 129), timeZone = "Europe/London" };

        var error = await Json.ErrorAsync(await Register(body), HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("password", Json.FieldNames(error));
    }

    [Fact]
    public async Task Password_is_not_trimmed()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email, password: "  spaced password  ");

        var withSpaces = await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/login",
            new { email, password = "  spaced password  " });
        var trimmed = await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/login",
            new { email, password = "spaced password" });

        Assert.Equal(HttpStatusCode.OK, withSpaces.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, trimmed.StatusCode);
    }

    [Fact]
    public async Task UTC_is_an_accepted_time_zone()
    {
        var body = new { name = "Ann", email = ApiFixture.NewEmail(), password = ApiFixture.Password, timeZone = "UTC" };
        await Json.ReadAsync(await Register(body), HttpStatusCode.Created);
    }
}
