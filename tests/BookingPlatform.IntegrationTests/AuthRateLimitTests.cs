using System.Net;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T4: /v1/auth/ endpoints are rate limited per client IP; over the limit the answer is
/// 429 RequestRejected with Retry-After (specs/07-nfr.md). Refresh and logout have their
/// own, looser bucket. The limits here are 3 and 5, not 10 and 60.
/// </summary>
public class AuthRateLimitTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly ApiFactory _api;

    public AuthRateLimitTests(TestDatabase database) => _api = new ApiFactory(database, authPermitLimit: 3, refreshPermitLimit: 5);

    public void Dispose() => _api.Dispose();

    private static object Credentials => new { email = ApiFixture.NewEmail(), password = "wrong password" };

    [Fact]
    public async Task Requests_over_the_limit_get_429_RequestRejected_with_Retry_After()
    {
        var client = _api.ClientFrom("203.0.113.10");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/v1/auth/login", Credentials)).StatusCode);
        }

        var response = await client.PostJsonAsync("/v1/auth/login", Credentials);

        await Json.ErrorAsync(response, HttpStatusCode.TooManyRequests, "RequestRejected");
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero, "Retry-After must give seconds to wait");
    }

    [Fact]
    public async Task Login_and_register_share_one_limit()
    {
        var client = _api.ClientFrom("203.0.113.20");
        await client.PostJsonAsync("/v1/auth/login", Credentials);
        await client.PostJsonAsync("/v1/auth/login", Credentials);
        await client.PostJsonAsync("/v1/auth/login", Credentials);

        var response = await client.PostJsonAsync("/v1/auth/register",
            new { name = "Ann", email = ApiFixture.NewEmail(), password = "12345678", timeZone = "UTC" });

        await Json.ErrorAsync(response, HttpStatusCode.TooManyRequests, "RequestRejected");
    }

    [Fact]
    public async Task Accept_invite_shares_the_login_limit()
    {
        // T7: accept-invite checks a secret, so it counts against the same budget (specs/07-nfr.md).
        var client = _api.ClientFrom("203.0.113.22");
        await client.PostJsonAsync("/v1/auth/login", Credentials);
        await client.PostJsonAsync("/v1/auth/login", Credentials);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/v1/auth/accept-invite",
            new { token = "unknown-token", password = "a long enough password" })).StatusCode);

        var response = await client.PostJsonAsync("/v1/auth/login", Credentials);

        await Json.ErrorAsync(response, HttpStatusCode.TooManyRequests, "RequestRejected");
    }

    [Fact]
    public async Task Forgot_and_reset_password_share_the_login_limit()
    {
        // T7a: forgot-password sends email and reset-password checks a secret (specs/07-nfr.md).
        var client = _api.ClientFrom("203.0.113.23");
        await client.PostJsonAsync("/v1/auth/login", Credentials);
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostJsonAsync("/v1/auth/forgot-password", new { email = ApiFixture.NewEmail() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/v1/auth/reset-password",
            new { token = "unknown-token", password = "a long enough password" })).StatusCode);

        var response = await client.PostJsonAsync("/v1/auth/forgot-password", new { email = ApiFixture.NewEmail() });

        await Json.ErrorAsync(response, HttpStatusCode.TooManyRequests, "RequestRejected");
    }

    [Fact]
    public async Task Change_password_shares_the_login_limit()
    {
        // T7a: slows guessing the current password with a stolen access token (specs/04-api.openapi.yaml).
        var registered = await Json.ReadAsync(await _api.ClientFrom("203.0.113.24").PostJsonAsync("/v1/auth/register",
            new { name = "Ann", email = ApiFixture.NewEmail(), password = "12345678", timeZone = "UTC" }), HttpStatusCode.Created);
        var client = _api.ClientFrom("203.0.113.25");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", registered.GetProperty("accessToken").GetString());
        var guess = new { currentPassword = "a wrong guess", newPassword = "a long enough password" };
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostJsonAsync("/v1/auth/change-password", guess)).StatusCode);
        }

        var response = await client.PostJsonAsync("/v1/auth/change-password", guess);

        await Json.ErrorAsync(response, HttpStatusCode.TooManyRequests, "RequestRejected");
    }

    [Fact]
    public async Task Refresh_and_logout_have_their_own_looser_limit()
    {
        // Many users behind one IP refresh every 15 minutes; that must not block their logins.
        var client = _api.ClientFrom("203.0.113.21");
        for (var i = 0; i < 3; i++) await client.PostAsync("/v1/auth/refresh", null);
        for (var i = 0; i < 2; i++) await client.PostAsync("/v1/auth/logout", null);

        // Login still has its whole budget...
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/v1/auth/login", Credentials)).StatusCode);

        // ...and the 6th refresh-or-logout is over the refresh limit of 5.
        await Json.ErrorAsync(await client.PostAsync("/v1/auth/refresh", null),
            HttpStatusCode.TooManyRequests, "RequestRejected");
    }

    [Fact]
    public async Task Each_client_IP_has_its_own_limit()
    {
        var busy = _api.ClientFrom("203.0.113.30");
        for (var i = 0; i < 4; i++) await busy.PostJsonAsync("/v1/auth/login", Credentials);

        var other = await _api.ClientFrom("203.0.113.31").PostJsonAsync("/v1/auth/login", Credentials);

        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task Other_endpoints_are_not_limited()
    {
        var client = _api.ClientFrom("203.0.113.40");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/me")).StatusCode);
        }
    }
}
