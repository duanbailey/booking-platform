using System.Net;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T4: POST /v1/auth/refresh and /v1/auth/logout over HTTP (ADR 0010). The rotation and
/// revocation rules themselves are tested against the service in RefreshTokenTests.
/// </summary>
public class TokenEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private Task<HttpResponseMessage> Refresh(string? token) =>
        fixture.Api.CreateClient().PostJsonAsync("/v1/auth/refresh", new { refreshToken = token });

    private Task<HttpResponseMessage> Logout(string? token) =>
        fixture.Api.CreateClient().PostJsonAsync("/v1/auth/logout", new { refreshToken = token });

    [Fact]
    public async Task Refresh_returns_new_tokens_that_work()
    {
        var login = await fixture.RegisterAsync();
        var oldRefresh = login.GetProperty("refreshToken").GetString();

        var tokens = await Json.ReadAsync(await Refresh(oldRefresh), HttpStatusCode.OK);

        Assert.NotEqual(oldRefresh, tokens.GetProperty("refreshToken").GetString());
        var me = await fixture.ClientWithToken(tokens.GetProperty("accessToken").GetString()!).GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_refresh_token_returns_401_and_ends_the_login()
    {
        var login = await fixture.RegisterAsync();
        var first = login.GetProperty("refreshToken").GetString();
        var second = (await Json.ReadAsync(await Refresh(first), HttpStatusCode.OK)).GetProperty("refreshToken").GetString();

        await Json.ErrorAsync(await Refresh(first), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
        await Json.ErrorAsync(await Refresh(second), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
    }

    [Fact]
    public async Task Unknown_refresh_token_returns_401_InvalidRefreshToken()
    {
        await Json.ErrorAsync(await Refresh("no-such-token"), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
    }

    [Fact]
    public async Task Refresh_needs_no_access_token()
    {
        // An expired access token is exactly when a client refreshes.
        var login = await fixture.RegisterAsync();
        var client = fixture.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "expired-or-garbage");

        var response = await client.PostJsonAsync("/v1/auth/refresh",
            new { refreshToken = login.GetProperty("refreshToken").GetString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Logout_returns_204_and_the_refresh_token_stops_working()
    {
        var login = await fixture.RegisterAsync();
        var refresh = login.GetProperty("refreshToken").GetString();

        var response = await Logout(refresh);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        await Json.ErrorAsync(await Refresh(refresh), HttpStatusCode.Unauthorized, "InvalidRefreshToken");
    }

    [Fact]
    public async Task Logout_returns_204_for_any_token()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Logout("no-such-token")).StatusCode);
    }

    [Theory]
    [InlineData("/v1/auth/refresh")]
    [InlineData("/v1/auth/logout")]
    public async Task Missing_refresh_token_returns_400_ValidationFailed(string path)
    {
        var response = await fixture.Api.CreateClient().PostAsync(path, Json.Body("{}"));

        var error = await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("refreshToken", Json.FieldNames(error));
    }
}
