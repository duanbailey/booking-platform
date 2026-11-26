using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>One migrated database and one API host, shared by the tests of a class.</summary>
public class ApiFixture : IAsyncLifetime
{
    public TestDatabase Database { get; } = new();
    public ApiFactory Api { get; private set; } = null!;
    public Sql Sql { get; private set; } = null!;

    public const string Password = "correct horse battery";

    /// <summary>The API's clock; null for the system clock.</summary>
    protected virtual TimeProvider? Time => null;

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        Api = new ApiFactory(Database, time: Time);
        Sql = new Sql(Database.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await Database.DisposeAsync();
    }

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.test";

    /// <summary>Signs up through the API and returns the AuthToken body.</summary>
    public async Task<JsonElement> RegisterAsync(string? email = null, string password = Password, string name = "Ann Example")
    {
        var response = await Api.CreateClient().PostJsonAsync("/v1/auth/register",
            new { name, email = email ?? NewEmail(), password, timeZone = "Europe/London" });
        return await Json.ReadAsync(response, HttpStatusCode.Created);
    }

    /// <summary>Signs up and returns the AuthToken body and the refresh token from its cookie (ADR 0013).</summary>
    public async Task<(JsonElement Body, string RefreshToken)> RegisterWithRefreshAsync(string? email = null, string password = Password)
    {
        var response = await Api.CreateClient().PostJsonAsync("/v1/auth/register",
            new { name = "Ann Example", email = email ?? NewEmail(), password, timeZone = "Europe/London" });
        return (await Json.ReadAsync(response, HttpStatusCode.Created), RefreshCookie.TokenFrom(response));
    }

    public HttpClient ClientWithToken(string accessToken)
    {
        var client = Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}

/// <summary>
/// An <see cref="ApiFixture"/> whose API clock stands still at the moment the fixture was
/// made (whole seconds), so tests can put an expiry exactly at "now". The clock stays near
/// the real time, so access tokens it issues are still accepted.
/// </summary>
public sealed class FrozenClockApiFixture : ApiFixture
{
    public MutableTimeProvider Clock { get; } =
        new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    protected override TimeProvider? Time => Clock;
}
