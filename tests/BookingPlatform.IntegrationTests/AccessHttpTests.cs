using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T3 "Done when": a removed staff member or a user from another business gets no
/// access, end to end through the HTTP pipeline (specs/07-nfr.md "Responses").
/// </summary>
public class AccessHttpTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly Sql _sql;
    private readonly ApiFactory _factory;

    public AccessHttpTests(TestDatabase database)
    {
        _sql = new Sql(database.ConnectionString);
        _factory = new ApiFactory(database);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record World(Guid Business, Guid Owner, Guid StaffUser, Guid StaffId, Guid Customer, Guid Booking);

    private async Task<World> WorldAsync()
    {
        var owner = await _sql.InsertUserAsync();
        var business = await _sql.InsertBusinessAsync(owner);
        var staffUser = await _sql.InsertUserAsync();
        var staffId = await _sql.InsertStaffAsync(business, staffUser);
        var customer = await _sql.InsertUserAsync();
        var booking = await _sql.InsertBookingAsync(
            new Sql.BookingFixture(business, staffId, await _sql.InsertServiceAsync(business), customer));
        return new World(business, owner, staffUser, staffId, customer, booking);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Customer_staff_member_and_owner_can_view_the_booking()
    {
        var w = await WorldAsync();
        foreach (var user in new[] { w.Customer, w.StaffUser, w.Owner })
        {
            var response = await _factory.ClientFor(user).GetAsync($"/test/bookings/{w.Booking}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Removed_staff_member_gets_404_for_their_old_booking()
    {
        var w = await WorldAsync();
        await _sql.RemoveStaffAsync(w.StaffId);

        var response = await _factory.ClientFor(w.StaffUser).GetAsync($"/test/bookings/{w.Booking}");

        await AssertError(response, HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Removal_takes_effect_immediately_for_a_token_issued_before_it()
    {
        var w = await WorldAsync();
        var client = _factory.ClientFor(w.StaffUser);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test/bookings/{w.Booking}")).StatusCode);

        await _sql.RemoveStaffAsync(w.StaffId);

        await AssertError(await client.GetAsync($"/test/bookings/{w.Booking}"), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Owner_and_staff_of_another_business_get_404_for_the_booking()
    {
        var w = await WorldAsync();
        var other = await WorldAsync();

        foreach (var user in new[] { other.Owner, other.StaffUser })
        {
            var response = await _factory.ClientFor(user).GetAsync($"/test/bookings/{w.Booking}");
            await AssertError(response, HttpStatusCode.NotFound, "NotFound");
        }
    }

    [Fact]
    public async Task Booking_that_does_not_exist_gets_the_same_404()
    {
        var w = await WorldAsync();
        var response = await _factory.ClientFor(w.Owner).GetAsync($"/test/bookings/{Guid.NewGuid()}");
        await AssertError(response, HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Owner_can_change_settings()
    {
        var w = await WorldAsync();
        var response = await _factory.ClientFor(w.Owner).PatchAsync($"/test/businesses/{w.Business}/settings", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Staff_member_and_customer_get_403_changing_settings_of_a_business_they_can_view()
    {
        var w = await WorldAsync();
        foreach (var user in new[] { w.StaffUser, w.Customer })
        {
            var response = await _factory.ClientFor(user).PatchAsync($"/test/businesses/{w.Business}/settings", null);
            await AssertError(response, HttpStatusCode.Forbidden, "Forbidden");
        }
    }

    [Fact]
    public async Task Owner_of_another_business_gets_403_changing_settings()
    {
        var w = await WorldAsync();
        var other = await WorldAsync();

        var response = await _factory.ClientFor(other.Owner).PatchAsync($"/test/businesses/{w.Business}/settings", null);

        await AssertError(response, HttpStatusCode.Forbidden, "Forbidden");
    }
}
