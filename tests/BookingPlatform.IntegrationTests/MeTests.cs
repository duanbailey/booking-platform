using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T4: GET and PATCH /v1/me: the caller's profile and roles (isAdmin, owned business,
/// active staff rows), and changing name or time zone.
/// </summary>
public class MeTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid UserId, HttpClient Client)> UserAsync()
    {
        var userId = await fixture.Sql.InsertUserAsync();
        return (userId, fixture.Api.ClientFor(userId));
    }

    private static HttpRequestMessage Patch(string json) =>
        new(HttpMethod.Patch, "/v1/me") { Content = Json.Body(json) };

    [Fact]
    public async Task Get_returns_the_profile()
    {
        var (userId, client) = await UserAsync();

        var me = await Json.ReadAsync(await client.GetAsync("/v1/me"), HttpStatusCode.OK);

        Assert.Equal(userId, me.GetProperty("id").GetGuid());
        Assert.Equal("Test User", me.GetProperty("name").GetString());
        Assert.Equal("Europe/London", me.GetProperty("timeZone").GetString());
        Assert.Equal(
            ["email", "id", "isAdmin", "name", "ownedBusinessId", "staff", "timeZone"],
            me.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Get_never_returns_the_password_hash()
    {
        var login = await fixture.RegisterAsync();
        var text = await (await fixture.ClientWithToken(login.GetProperty("accessToken").GetString()!)
            .GetAsync("/v1/me")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAAA", text); // PasswordHasher's v3 format prefix
    }

    [Fact]
    public async Task Admin_owner_and_staff_roles_are_reported()
    {
        var (userId, client) = await UserAsync();
        await fixture.Sql.InsertAdminAsync(userId);
        var owned = await fixture.Sql.InsertBusinessAsync(userId);
        var ownStaff = await fixture.Sql.InsertStaffAsync(owned, userId);
        var elsewhere = await fixture.Sql.InsertBusinessAsync();
        var otherStaff = await fixture.Sql.InsertStaffAsync(elsewhere, userId);

        var me = await Json.ReadAsync(await client.GetAsync("/v1/me"), HttpStatusCode.OK);

        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(owned, me.GetProperty("ownedBusinessId").GetGuid());
        var staff = me.GetProperty("staff").EnumerateArray()
            .Select(s => (s.GetProperty("staffId").GetGuid(), s.GetProperty("businessId").GetGuid()))
            .OrderBy(s => s.Item1).ToList();
        Assert.Equal(new[] { (ownStaff, owned), (otherStaff, elsewhere) }.OrderBy(s => s.Item1), staff);
    }

    [Fact]
    public async Task Removed_staff_rows_are_not_listed()
    {
        var (userId, client) = await UserAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(await fixture.Sql.InsertBusinessAsync(), userId);
        await fixture.Sql.RemoveStaffAsync(staffId);

        var me = await Json.ReadAsync(await client.GetAsync("/v1/me"), HttpStatusCode.OK);

        Assert.Empty(me.GetProperty("staff").EnumerateArray());
    }

    [Fact]
    public async Task Get_without_a_token_returns_401()
    {
        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync("/v1/me"), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Valid_token_for_a_user_that_does_not_exist_returns_401()
    {
        var response = await fixture.Api.ClientFor(Guid.NewGuid()).GetAsync("/v1/me");
        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Patch_changes_only_the_fields_sent()
    {
        var (_, client) = await UserAsync();

        var renamed = await Json.ReadAsync(await client.SendAsync(Patch("""{"name":"Ann Renamed"}""")), HttpStatusCode.OK);
        Assert.Equal("Ann Renamed", renamed.GetProperty("name").GetString());
        Assert.Equal("Europe/London", renamed.GetProperty("timeZone").GetString());

        var moved = await Json.ReadAsync(await client.SendAsync(Patch("""{"timeZone":"America/New_York"}""")), HttpStatusCode.OK);
        Assert.Equal("Ann Renamed", moved.GetProperty("name").GetString());
        Assert.Equal("America/New_York", moved.GetProperty("timeZone").GetString());

        // The change is saved, not just echoed.
        var me = await Json.ReadAsync(await client.GetAsync("/v1/me"), HttpStatusCode.OK);
        Assert.Equal("America/New_York", me.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task Patch_both_fields_at_once()
    {
        var (_, client) = await UserAsync();

        var me = await Json.ReadAsync(
            await client.SendAsync(Patch("""{"name":"Bo","timeZone":"Asia/Tokyo"}""")), HttpStatusCode.OK);

        Assert.Equal("Bo", me.GetProperty("name").GetString());
        Assert.Equal("Asia/Tokyo", me.GetProperty("timeZone").GetString());
    }

    [Theory]
    [InlineData("""{}""", "body")]
    [InlineData("""{"name":null}""", "name")]
    [InlineData("""{"timeZone":null}""", "timeZone")]
    [InlineData("""{"name":""}""", "name")]
    [InlineData("""{"name":"   "}""", "name")]
    [InlineData("""{"timeZone":"GMT Standard Time"}""", "timeZone")]
    public async Task Invalid_patch_returns_400_ValidationFailed(string json, string field)
    {
        var (_, client) = await UserAsync();

        var error = await Json.ErrorAsync(await client.SendAsync(Patch(json)), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Theory]
    [InlineData("""{"email":"new@example.test"}""")]
    [InlineData("""{"isAdmin":true}""")]
    public async Task Patch_cannot_change_other_fields(string json)
    {
        var (userId, client) = await UserAsync();

        await Json.ErrorAsync(await client.SendAsync(Patch(json)), HttpStatusCode.BadRequest, "ValidationFailed");

        var me = await Json.ReadAsync(await client.GetAsync("/v1/me"), HttpStatusCode.OK);
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.NotEqual("new@example.test", me.GetProperty("email").GetString());
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Patch_trims_the_name()
    {
        var (_, client) = await UserAsync();

        var me = await Json.ReadAsync(await client.SendAsync(Patch("""{"name":"  Bo  "}""")), HttpStatusCode.OK);

        Assert.Equal("Bo", me.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Patch_with_a_valid_token_for_a_user_that_does_not_exist_returns_401()
    {
        var response = await fixture.Api.ClientFor(Guid.NewGuid()).SendAsync(Patch("""{"name":"X"}"""));
        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Patch_without_a_token_returns_401()
    {
        var response = await fixture.Api.CreateClient().SendAsync(Patch("""{"name":"X"}"""));
        await Json.ErrorAsync(response, HttpStatusCode.Unauthorized, "Unauthenticated");
    }
}
