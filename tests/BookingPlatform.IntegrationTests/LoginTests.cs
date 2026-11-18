using System.Net;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T4: POST /v1/auth/login. Stories "Owner/Staff/Customer can log in"; an account with no
/// password cannot log in; every failure looks the same (specs/07-nfr.md).
/// </summary>
public class LoginTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private Task<HttpResponseMessage> Login(string email, string password) =>
        fixture.Api.CreateClient().PostJsonAsync("/v1/auth/login", new { email, password });

    [Fact]
    public async Task Valid_credentials_log_the_user_in()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);

        var tokens = await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);

        var me = await fixture.ClientWithToken(tokens.GetProperty("accessToken").GetString()!).GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Owner_and_staff_log_in_the_same_way_and_see_their_roles()
    {
        // Roles come from the data, not from how the user logs in (specs/07-nfr.md).
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);
        var userId = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM users WHERE email = @email", new { email }))!);
        var business = await fixture.Sql.InsertBusinessAsync(userId);
        var staffId = await fixture.Sql.InsertStaffAsync(business, userId);

        var tokens = await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);
        var me = await Json.ReadAsync(
            await fixture.ClientWithToken(tokens.GetProperty("accessToken").GetString()!).GetAsync("/v1/me"),
            HttpStatusCode.OK);

        Assert.Equal(business, me.GetProperty("ownedBusinessId").GetGuid());
        var staff = Assert.Single(me.GetProperty("staff").EnumerateArray());
        Assert.Equal(staffId, staff.GetProperty("staffId").GetGuid());
    }

    [Fact]
    public async Task Email_is_matched_ignoring_case_and_surrounding_spaces()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);

        await Json.ReadAsync(await Login($" {email.ToUpperInvariant()} ", ApiFixture.Password), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_password_unknown_email_and_account_without_password_get_the_same_401()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);
        var invited = ApiFixture.NewEmail();
        await fixture.Sql.InsertUserAsync(invited); // no password yet

        var bodies = new List<string>();
        foreach (var response in new[]
        {
            await Login(email, "wrong password"),
            await Login(ApiFixture.NewEmail(), ApiFixture.Password),
            await Login(invited, ApiFixture.Password),
        })
        {
            await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "InvalidCredentials");
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task Login_upgrades_a_hash_in_an_older_format()
    {
        // specs/07-nfr.md: the versioned hash format lets a hash be upgraded at the next login.
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);
        var oldHasher = new PasswordHasher<object>(Options.Create(
            new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
        var oldHash = oldHasher.HashPassword(new object(), ApiFixture.Password);
        await fixture.Sql.ExecuteAsync("UPDATE users SET password_hash = @oldHash WHERE id = @userId", new { oldHash, userId });

        await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);

        var newHash = (string)(await fixture.Sql.ScalarAsync("SELECT password_hash FROM users WHERE id = @userId", new { userId }))!;
        Assert.NotEqual(oldHash, newHash);
        Assert.Equal(0x01, Convert.FromBase64String(newHash)[0]); // format marker: 0x00 = V2, 0x01 = V3
        await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_password_returns_400_ValidationFailed()
    {
        var response = await fixture.Api.CreateClient().PostAsync("/v1/auth/login",
            Json.Body("""{"email":"a@example.test"}"""));

        var error = await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("password", Json.FieldNames(error));
    }

    [Fact]
    public async Task Each_login_starts_a_separate_session()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);

        var phone = await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);
        var laptop = await Json.ReadAsync(await Login(email, ApiFixture.Password), HttpStatusCode.OK);

        // Logging out the phone leaves the laptop logged in (ADR 0010).
        await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/logout",
            new { refreshToken = phone.GetProperty("refreshToken").GetString() });
        var refresh = await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/refresh",
            new { refreshToken = laptop.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }
}
