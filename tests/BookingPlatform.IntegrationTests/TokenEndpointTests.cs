using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T4, T32: the refresh token cookie over HTTP (ADR 0010, ADR 0013). Every response that starts or renews a login
/// sets <c>refresh_token</c> (HttpOnly; Secure; SameSite=Strict; Path=/v1/auth; Max-Age to the token's expiry) and
/// carries no refresh token in its body; refresh and logout read only the cookie. Decided 2026-11-26 (plan/tasks.md,
/// T32): a failed refresh clears the cookie; every response that sets or clears it says Cache-Control: no-store;
/// an empty cookie counts as none; a body sent to refresh or logout is ignored. After review (2026-11-26): the
/// cookie is cleared only when the request carried one, so a cross-site form post (which SameSite=Strict sends
/// without the cookie) can't log the user out. The rotation
/// and revocation rules themselves are tested against the service in RefreshTokenTests.
/// </summary>
public class TokenEndpointTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private static readonly int ThirtyDays = (int)TimeSpan.FromDays(30).TotalSeconds;

    private void AssertSetsCookie(HttpResponseMessage response, JsonElement body)
    {
        var cookie = RefreshCookie.Parse(response);
        Assert.False(string.IsNullOrEmpty(cookie.Value));
        Assert.True(cookie.Has("HttpOnly"));
        Assert.True(cookie.Has("Secure"));
        Assert.Equal("strict", cookie["SameSite"]?.ToLowerInvariant());
        Assert.Equal("/v1/auth", cookie["Path"]);
        Assert.Equal(ThirtyDays.ToString(), cookie["Max-Age"]); // the frozen clock: the token expires in exactly 30 days
        Assert.False(cookie.Has("Expires"));
        Assert.False(cookie.Has("Domain")); // host-only

        Assert.False(body.TryGetProperty("refreshToken", out _)); // ADR 0013: never in a body
        Assert.Equal(fixture.Now.AddDays(30), body.GetProperty("refreshTokenExpiresAt").GetDateTime().ToUniversalTime());
        AssertNoStore(response);
    }

    private static void AssertClearsCookie(HttpResponseMessage response)
    {
        var cookie = RefreshCookie.Parse(response);
        Assert.Equal("", cookie.Value);
        Assert.Equal("0", cookie["Max-Age"]);
        Assert.Equal("/v1/auth", cookie["Path"]); // the same path, or the browser keeps the real cookie
        Assert.True(cookie.Has("HttpOnly"));
        Assert.True(cookie.Has("Secure"));
        Assert.Equal("strict", cookie["SameSite"]?.ToLowerInvariant());
        AssertNoStore(response);
    }

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

    private async Task<Guid> UserIdAsync(string email) =>
        Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync("SELECT id FROM users WHERE email = @email", new { email }))!);

    private async Task<bool> RefreshWorksAsync(string token) =>
        (await fixture.Api.RefreshAsync(token)).StatusCode == HttpStatusCode.OK;

    // ------------------------------------------------------------ Responses that start or renew a login

    [Fact]
    public async Task Register_sets_the_cookie()
    {
        var response = await RefreshCookie.Client(fixture.Api).PostJsonAsync("/v1/auth/register",
            new { name = "Ann Example", email = ApiFixture.NewEmail(), password = ApiFixture.Password, timeZone = "Europe/London" });

        AssertSetsCookie(response, await Json.ReadAsync(response, HttpStatusCode.Created));
        Assert.True(await RefreshWorksAsync(RefreshCookie.TokenFrom(response)));
    }

    [Fact]
    public async Task Login_sets_the_cookie()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);

        var response = await RefreshCookie.Client(fixture.Api).PostJsonAsync("/v1/auth/login", new { email, password = ApiFixture.Password });

        AssertSetsCookie(response, await Json.ReadAsync(response, HttpStatusCode.OK));
        Assert.True(await RefreshWorksAsync(RefreshCookie.TokenFrom(response)));
    }

    [Fact]
    public async Task Accept_invite_sets_the_cookie()
    {
        var userId = await fixture.Sql.InsertUserAsync(ApiFixture.NewEmail());
        var token = Guid.NewGuid().ToString("N");
        await fixture.Sql.InsertInviteAsync(userId, token, expiresAt: fixture.Now.AddDays(7));

        var response = await RefreshCookie.Client(fixture.Api).PostJsonAsync("/v1/auth/accept-invite", new { token, password = ApiFixture.Password });

        AssertSetsCookie(response, await Json.ReadAsync(response, HttpStatusCode.OK));
        Assert.True(await RefreshWorksAsync(RefreshCookie.TokenFrom(response)));
    }

    [Fact]
    public async Task Reset_password_sets_the_cookie()
    {
        var email = ApiFixture.NewEmail();
        await fixture.RegisterAsync(email);
        var token = Guid.NewGuid().ToString("N");
        await fixture.Sql.InsertPasswordResetAsync(await UserIdAsync(email), token, expiresAt: fixture.Now.AddHours(1), createdAt: fixture.Now.AddMinutes(-1));

        var response = await RefreshCookie.Client(fixture.Api).PostJsonAsync("/v1/auth/reset-password", new { token, password = "a new password" });

        AssertSetsCookie(response, await Json.ReadAsync(response, HttpStatusCode.OK));
        Assert.True(await RefreshWorksAsync(RefreshCookie.TokenFrom(response)));
    }

    [Fact]
    public async Task Change_password_sets_the_cookie()
    {
        var (login, _) = await fixture.RegisterWithRefreshAsync();
        var client = RefreshCookie.Client(fixture.Api, login.GetProperty("accessToken").GetString());

        var response = await client.PostJsonAsync("/v1/auth/change-password",
            new { currentPassword = ApiFixture.Password, newPassword = "a new password" });

        AssertSetsCookie(response, await Json.ReadAsync(response, HttpStatusCode.OK));
        Assert.True(await RefreshWorksAsync(RefreshCookie.TokenFrom(response)));
    }

    [Fact]
    public async Task Refresh_reads_the_cookie_and_sets_a_new_one_whose_access_token_works()
    {
        var (_, first) = await fixture.RegisterWithRefreshAsync();

        var response = await fixture.Api.RefreshAsync(first);

        var body = await Json.ReadAsync(response, HttpStatusCode.OK);
        AssertSetsCookie(response, body);
        var second = RefreshCookie.TokenFrom(response);
        Assert.NotEqual(first, second);
        var me = await fixture.ClientWithToken(body.GetProperty("accessToken").GetString()!).GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True(await RefreshWorksAsync(second));
    }

    [Fact]
    public async Task A_failed_login_sets_no_cookie()
    {
        var response = await RefreshCookie.Client(fixture.Api).PostJsonAsync("/v1/auth/login",
            new { email = ApiFixture.NewEmail(), password = "wrong password" });

        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "InvalidCredentials");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    public static TheoryData<string> FailedLoginStarts => new()
    {
        "register-email-taken", "change-password-wrong-current", "change-password-no-access-token",
        "accept-invite-unknown-token", "reset-password-unknown-token", "register-invalid",
    };

    [Theory]
    [MemberData(nameof(FailedLoginStarts))]
    public async Task A_refused_request_to_an_endpoint_that_starts_a_login_sets_no_cookie(string scenario)
    {
        var client = RefreshCookie.Client(fixture.Api);
        HttpResponseMessage response;
        switch (scenario)
        {
            case "register-email-taken":
            {
                var email = ApiFixture.NewEmail();
                await fixture.RegisterAsync(email);
                response = await client.PostJsonAsync("/v1/auth/register",
                    new { name = "Ann Example", email, password = ApiFixture.Password, timeZone = "Europe/London" });
                await Json.ErrorAsync(response, HttpStatusCode.Conflict, "EmailTaken");
                break;
            }
            case "register-invalid":
                response = await client.PostJsonAsync("/v1/auth/register", new { name = "Ann Example" });
                await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
                break;
            case "change-password-wrong-current":
            {
                var (login, _) = await fixture.RegisterWithRefreshAsync();
                response = await RefreshCookie.Client(fixture.Api, login.GetProperty("accessToken").GetString())
                    .PostJsonAsync("/v1/auth/change-password", new { currentPassword = "wrong password", newPassword = "a new password" });
                await Json.ErrorAsync(response, HttpStatusCode.UnprocessableEntity, "CurrentPasswordIncorrect");
                break;
            }
            case "change-password-no-access-token":
                response = await client.PostJsonAsync("/v1/auth/change-password",
                    new { currentPassword = ApiFixture.Password, newPassword = "a new password" });
                await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "Unauthenticated");
                break;
            case "accept-invite-unknown-token":
                response = await client.PostJsonAsync("/v1/auth/accept-invite", new { token = "no-such-token", password = ApiFixture.Password });
                await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "InviteInvalid");
                break;
            case "reset-password-unknown-token":
                response = await client.PostJsonAsync("/v1/auth/reset-password", new { token = "no-such-token", password = ApiFixture.Password });
                await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "ResetLinkInvalid");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // ------------------------------------------------------------ Refresh

    [Fact]
    public async Task Reusing_a_refresh_token_returns_401_clears_the_cookie_and_ends_the_login()
    {
        var (_, first) = await fixture.RegisterWithRefreshAsync();
        var second = RefreshCookie.TokenFrom(await fixture.Api.RefreshAsync(first));

        var reuse = await fixture.Api.RefreshAsync(first);

        await Json.ErrorAsync(reuse, HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        AssertClearsCookie(reuse);
        await Json.ErrorAsync(await fixture.Api.RefreshAsync(second), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
    }

    [Fact]
    public async Task Refresh_with_an_unknown_token_returns_401_and_clears_the_cookie()
    {
        var response = await fixture.Api.RefreshAsync("no-such-token");

        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        AssertClearsCookie(response);
    }

    [Theory]
    [InlineData(null)] // no cookie: what a cross-site form post sends (SameSite=Strict)
    [InlineData("")] // an empty cookie counts as none
    public async Task Refresh_without_a_cookie_returns_401_and_leaves_cookies_alone(string? cookie)
    {
        var response = await fixture.Api.RefreshAsync(cookie);

        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Refresh_ignores_a_refresh_token_in_the_body()
    {
        var (_, token) = await fixture.RegisterWithRefreshAsync();

        var response = await fixture.Api.RefreshAsync(null, Json.Body(JsonSerializer.Serialize(new { refreshToken = token })));

        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        Assert.True(await RefreshWorksAsync(token)); // the token sent in the body was never looked at
    }

    [Theory]
    [InlineData("{\"refreshToken\":")] // malformed JSON
    [InlineData("{\"anything\": 1}")]
    public async Task Refresh_ignores_any_body(string body)
    {
        var (_, token) = await fixture.RegisterWithRefreshAsync();

        var response = await fixture.Api.RefreshAsync(token, Json.Body(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_needs_no_access_token()
    {
        // An expired access token is exactly when a client refreshes.
        var (_, token) = await fixture.RegisterWithRefreshAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        request.Headers.Add("Cookie", $"{RefreshCookie.Name}={token}");
        request.Headers.Authorization = new("Bearer", "expired-or-garbage");

        var response = await RefreshCookie.Client(fixture.Api).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------ Logout

    [Fact]
    public async Task Logout_returns_204_clears_the_cookie_and_the_refresh_token_stops_working()
    {
        var (_, token) = await fixture.RegisterWithRefreshAsync();

        var response = await fixture.Api.LogoutAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        AssertClearsCookie(response);
        Assert.False(await RefreshWorksAsync(token));
    }

    [Fact]
    public async Task Logout_with_an_unknown_token_returns_204_and_clears_the_cookie()
    {
        var response = await fixture.Api.LogoutAsync("no-such-token");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertClearsCookie(response);
    }

    [Theory]
    [InlineData(null)] // no cookie: what a cross-site form post sends (SameSite=Strict)
    [InlineData("")]
    public async Task Logout_without_a_cookie_returns_204_and_leaves_cookies_alone(string? cookie)
    {
        var response = await fixture.Api.LogoutAsync(cookie);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_cross_site_form_post_cannot_log_the_user_out()
    {
        // A page elsewhere auto-submits <form method=post action=/v1/auth/logout>: a urlencoded body, and no
        // cookie, because SameSite=Strict keeps it off cross-site requests. Nothing is cleared or revoked.
        var (_, token) = await fixture.RegisterWithRefreshAsync();
        var form = new FormUrlEncodedContent([]);

        var logout = await fixture.Api.LogoutAsync(null, form);
        var refresh = await fixture.Api.RefreshAsync(null, new FormUrlEncodedContent([]));

        Assert.False(logout.Headers.Contains("Set-Cookie"));
        Assert.False(refresh.Headers.Contains("Set-Cookie"));
        Assert.True(await RefreshWorksAsync(token));
    }

    [Fact]
    public async Task Logout_ignores_a_refresh_token_in_the_body()
    {
        var (_, token) = await fixture.RegisterWithRefreshAsync();

        var response = await fixture.Api.LogoutAsync(null, Json.Body(JsonSerializer.Serialize(new { refreshToken = token })));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await RefreshWorksAsync(token)); // still logged in: the body was ignored
    }

    [Fact]
    public async Task Logout_ignores_a_malformed_body()
    {
        var (_, token) = await fixture.RegisterWithRefreshAsync();

        var response = await fixture.Api.LogoutAsync(token, Json.Body("{\"refreshToken\":"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await RefreshWorksAsync(token));
    }
}
