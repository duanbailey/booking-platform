using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>One migrated database and one API host, shared by the tests of a class.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public TestDatabase Database { get; } = new();
    public ApiFactory Api { get; private set; } = null!;
    public Sql Sql { get; private set; } = null!;

    public const string Password = "correct horse battery";

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        Api = new ApiFactory(Database);
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

    public HttpClient ClientWithToken(string accessToken)
    {
        var client = Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}
