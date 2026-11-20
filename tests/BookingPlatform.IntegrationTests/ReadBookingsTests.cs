using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T16: GET /v1/bookings/{bookingId}, /v1/me/bookings, /v1/businesses/{businessId}/bookings and
/// /v1/bookings/{bookingId}/history. Authorization table in specs/07-nfr.md; ADR 0003 (history).
/// Decisions 2026-11-20 are in plan/tasks.md (T16). Bookings and history rows are inserted directly;
/// reads don't depend on the clock.
/// </summary>
public class ReadBookingsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly DateTime Base = new(2026, 12, 7, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Shop(Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB, Guid CustomerId);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        var service = await fixture.Sql.InsertServiceAsync(business);
        var userA = await fixture.Sql.InsertUserAsync(name: "Ann Staff");
        var userB = await fixture.Sql.InsertUserAsync(name: "Ben Staff");
        return new Shop(owner, business, service, await fixture.Sql.InsertStaffAsync(business, userA), userA,
            await fixture.Sql.InsertStaffAsync(business, userB), userB, await fixture.Sql.InsertUserAsync(name: "Cara Customer"));
    }

    private async Task<Guid> BookingAsync(Shop shop, Guid staffId, DateTime start, Guid? customerId = null, bool cancelled = false, Guid? serviceId = null)
    {
        var parameters = Sql.BookingParameters(new(shop.BusinessId, staffId, serviceId ?? shop.ServiceId, customerId ?? shop.CustomerId));
        parameters["start"] = start;
        parameters["end"] = start.AddHours(1);
        parameters["blockedStart"] = start.AddMinutes(-15);
        parameters["blockedEnd"] = start.AddMinutes(75);
        if (cancelled)
        {
            parameters["status"] = "Cancelled";
            parameters["cancelledAt"] = Base.AddDays(-1);
            parameters["cancelledBy"] = shop.OwnerId;
            parameters["reason"] = "BusinessCancelled";
        }
        await fixture.Sql.ExecuteAsync(Sql.InsertBookingSql, parameters);
        return (Guid)parameters["id"]!;
    }

    private Task<HttpResponseMessage> Get(Guid callerId, string path) => fixture.Api.ClientFor(callerId).GetAsync(path);

    private async Task<JsonElement> OkAsync(Guid callerId, string path) => await Json.ReadAsync(await Get(callerId, path), HttpStatusCode.OK);

    private static List<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();

    /// <summary>Follows nextCursor to the end (at most 20 pages) and returns every id in order.</summary>
    private async Task<List<Guid>> AllPagesAsync(Guid callerId, string path, int limit)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var pages = 0; pages < 20; pages++)
        {
            var separator = path.Contains('?') ? '&' : '?';
            var page = await OkAsync(callerId, $"{path}{separator}limit={limit}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.True(page.GetProperty("items").GetArrayLength() <= limit);
            ids.AddRange(Ids(page));
            if (page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null) return ids;
            cursor = page.GetProperty("nextCursor").GetString();
        }
        throw new Xunit.Sdk.XunitException("Paging did not end.");
    }

    // ------------------------------------------------------------ GET /v1/bookings/{bookingId}

    [Fact]
    public async Task The_customer_the_booking_staff_member_and_the_owner_view_a_booking()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, shop.StaffA, Base);

        foreach (var caller in new[] { shop.CustomerId, shop.UserA, shop.OwnerId })
        {
            var body = await OkAsync(caller, $"/v1/bookings/{id}");

            Assert.Equal(
                ["businessId", "businessTimeZone", "cancellationReason", "cancelledAt", "customer", "endUtc", "id", "serviceId", "staffId", "startUtc", "status"],
                body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(id, body.GetProperty("id").GetGuid());
            Assert.Equal(shop.BusinessId, body.GetProperty("businessId").GetGuid());
            Assert.Equal("Europe/London", body.GetProperty("businessTimeZone").GetString());
            Assert.Equal(shop.StaffA, body.GetProperty("staffId").GetGuid());
            Assert.Equal(Iso(Base), body.GetProperty("startUtc").GetString());
            Assert.Equal(Iso(Base.AddHours(1)), body.GetProperty("endUtc").GetString());
            Assert.Equal("Confirmed", body.GetProperty("status").GetString());
            Assert.Equal("Cara Customer", body.GetProperty("customer").GetProperty("name").GetString());
            Assert.Equal(shop.CustomerId, body.GetProperty("customer").GetProperty("userId").GetGuid());
            Assert.EndsWith("@example.test", body.GetProperty("customer").GetProperty("email").GetString());
        }
    }

    [Fact]
    public async Task A_cancelled_booking_shows_when_and_why()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, shop.StaffA, Base, cancelled: true);

        var body = await OkAsync(shop.CustomerId, $"/v1/bookings/{id}");

        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(Iso(Base.AddDays(-1)), body.GetProperty("cancelledAt").GetString());
        Assert.Equal("BusinessCancelled", body.GetProperty("cancellationReason").GetString());
    }

    [Fact]
    public async Task Anyone_else_gets_404_for_a_booking()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, shop.StaffA, Base);
        var removedUser = await fixture.Sql.InsertUserAsync();
        var removedStaff = await fixture.Sql.InsertStaffAsync(shop.BusinessId, removedUser);
        var removedBooking = await BookingAsync(shop, removedStaff, Base.AddDays(1));
        await fixture.Sql.RemoveStaffAsync(removedStaff);

        var otherOwner = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertBusinessAsync(otherOwner);

        foreach (var caller in new[] { await fixture.Sql.InsertUserAsync(), shop.UserB, await fixture.Sql.InsertAdminUserAsync(), otherOwner })
        {
            await Json.ErrorAsync(await Get(caller, $"/v1/bookings/{id}"), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await Get(removedUser, $"/v1/bookings/{removedBooking}"), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Get(shop.CustomerId, $"/v1/bookings/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync($"/v1/bookings/{id}"), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task A_new_booking_Location_and_its_creation_history_can_be_read_back()
    {
        // End to end with T14: book through the API, follow Location, then read the creation row.
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @id", new { id = shop.BusinessId });
        await fixture.Sql.AssignStaffToServiceAsync(shop.StaffA, shop.ServiceId);
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await fixture.Sql.ExecuteAsync(
                "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staff, @day, '09:00', '12:00')",
                new { id = Guid.NewGuid(), staff = shop.StaffA, day });
        }
        var start = DateTime.UtcNow.Date.AddDays(3).AddHours(10);
        var created = await fixture.Api.ClientFor(shop.CustomerId).PostJsonAsync("/v1/bookings",
            new { serviceId = shop.ServiceId, staffId = shop.StaffA, startUtc = Iso(start) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var booking = await OkAsync(shop.CustomerId, created.Headers.Location!.OriginalString);
        var history = Assert.Single((await OkAsync(shop.CustomerId, $"{created.Headers.Location.OriginalString}/history")).GetProperty("items").EnumerateArray());

        Assert.Equal(Iso(start), booking.GetProperty("startUtc").GetString());
        Assert.Equal("Created", history.GetProperty("type").GetString());
        Assert.Equal("Customer", history.GetProperty("actor").GetProperty("role").GetString());
        Assert.Equal("Cara Customer", history.GetProperty("actor").GetProperty("name").GetString());
        Assert.False(history.TryGetProperty("previousSlot", out _));
    }

    // ------------------------------------------------------------ GET /v1/me/bookings

    [Fact]
    public async Task My_bookings_are_mine_as_customer_across_businesses_ordered_by_start_then_id()
    {
        var shop = await ShopAsync();
        var elsewhere = await ShopAsync();
        var me = shop.CustomerId;
        var later = await BookingAsync(shop, shop.StaffA, Base.AddDays(2));
        var cancelled = await BookingAsync(shop, shop.StaffB, Base.AddDays(1), cancelled: true);
        var otherBusiness = await BookingAsync(elsewhere, elsewhere.StaffA, Base.AddDays(1).AddHours(2), customerId: me);
        var tieA = await BookingAsync(shop, shop.StaffA, Base);
        var tieB = await BookingAsync(shop, shop.StaffB, Base);
        await BookingAsync(shop, shop.StaffA, Base.AddDays(3), customerId: await fixture.Sql.InsertUserAsync()); // someone else's
        // As the staff member, not the customer: not in my list.
        await BookingAsync(shop, shop.StaffA, Base.AddDays(4), customerId: shop.UserB);

        var page = await OkAsync(me, "/v1/me/bookings");

        var ties = new[] { tieA, tieB }.OrderBy(i => i.ToString(), StringComparer.Ordinal);
        Assert.Equal([.. ties, cancelled, otherBusiness, later], Ids(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.Equal(elsewhere.BusinessId, page.GetProperty("items")[3].GetProperty("businessId").GetGuid());
        Assert.Empty(Ids(await OkAsync(shop.UserA, "/v1/me/bookings")));
    }

    [Fact]
    public async Task My_bookings_filter_by_status_and_by_start_from_inclusive_to_exclusive()
    {
        var shop = await ShopAsync();
        var me = shop.CustomerId;
        var first = await BookingAsync(shop, shop.StaffA, Base);
        var second = await BookingAsync(shop, shop.StaffA, Base.AddDays(1), cancelled: true);
        var third = await BookingAsync(shop, shop.StaffA, Base.AddDays(2));

        Assert.Equal([first, third], Ids(await OkAsync(me, "/v1/me/bookings?status=Confirmed")));
        Assert.Equal([second], Ids(await OkAsync(me, "/v1/me/bookings?status=Cancelled")));
        Assert.Equal([first, second], Ids(await OkAsync(me, $"/v1/me/bookings?from={Iso(Base)}&to={Iso(Base.AddDays(2))}")));
        Assert.Equal([second, third], Ids(await OkAsync(me, $"/v1/me/bookings?from={Iso(Base.AddSeconds(1))}")));
        Assert.Equal([third], Ids(await OkAsync(me, $"/v1/me/bookings?status=Confirmed&from={Iso(Base.AddSeconds(1))}")));
    }

    [Fact]
    public async Task My_bookings_page_through_every_item_once()
    {
        var shop = await ShopAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 5; i++) expected.Add(await BookingAsync(shop, shop.StaffA, Base.AddDays(i)));
        expected.Add(await BookingAsync(shop, shop.StaffB, Base.AddDays(4))); // a tie at the last start
        expected = [.. expected.Take(4), .. expected.Skip(4).OrderBy(i => i.ToString(), StringComparer.Ordinal)];

        Assert.Equal(expected, await AllPagesAsync(shop.CustomerId, "/v1/me/bookings", limit: 2));
        Assert.Equal(expected.Skip(1), await AllPagesAsync(shop.CustomerId, $"/v1/me/bookings?from={Iso(Base.AddDays(1))}", limit: 4));
    }

    [Theory]
    [InlineData("status=Pending", "status")]
    [InlineData("from=2026-12-01T10:00:00", "from")]
    [InlineData("to=yesterday", "to")]
    [InlineData("from=2026-12-02T00:00:00Z&to=2026-12-01T00:00:00Z", "from")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("cursor=not-a-cursor", "cursor")]
    public async Task Invalid_list_queries_are_ValidationFailed_naming_the_parameter(string query, string field)
    {
        var shop = await ShopAsync();

        foreach (var path in new[] { "/v1/me/bookings", $"/v1/businesses/{shop.BusinessId}/bookings" })
        {
            var error = await Json.ErrorAsync(await Get(shop.OwnerId, $"{path}?{query}"), HttpStatusCode.BadRequest, "ValidationFailed");
            Assert.Contains(field, Json.FieldNames(error));
        }
    }

    [Fact]
    public async Task Lists_need_signing_in()
    {
        var shop = await ShopAsync();
        var anonymous = fixture.Api.CreateClient();

        await Json.ErrorAsync(await anonymous.GetAsync("/v1/me/bookings"), HttpStatusCode.Unauthorized, "Unauthenticated");
        await Json.ErrorAsync(await anonymous.GetAsync($"/v1/businesses/{shop.BusinessId}/bookings"), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    // ------------------------------------------------------------ GET /v1/businesses/{businessId}/bookings

    [Fact]
    public async Task The_owner_lists_every_booking_in_the_business_and_nothing_else()
    {
        var shop = await ShopAsync();
        var elsewhere = await ShopAsync();
        var b1 = await BookingAsync(shop, shop.StaffB, Base.AddDays(1), cancelled: true);
        var a1 = await BookingAsync(shop, shop.StaffA, Base, customerId: await fixture.Sql.InsertUserAsync());
        var a2 = await BookingAsync(shop, shop.StaffA, Base.AddDays(2));
        await BookingAsync(elsewhere, elsewhere.StaffA, Base.AddHours(3));

        Assert.Equal([a1, b1, a2], Ids(await OkAsync(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings")));
        Assert.Equal([a1, a2], Ids(await OkAsync(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings?staffId={shop.StaffA}")));
        Assert.Equal([a1, a2], Ids(await OkAsync(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings?status=Confirmed")));
        Assert.Equal([b1], Ids(await OkAsync(shop.OwnerId,
            $"/v1/businesses/{shop.BusinessId}/bookings?from={Iso(Base.AddHours(1))}&to={Iso(Base.AddDays(2))}")));
    }

    [Fact]
    public async Task The_owner_may_filter_by_a_removed_member_but_not_by_a_staffId_outside_the_business()
    {
        var shop = await ShopAsync();
        var elsewhere = await ShopAsync();
        var removedStaff = await fixture.Sql.InsertStaffAsync(shop.BusinessId);
        var past = await BookingAsync(shop, removedStaff, Base);
        await fixture.Sql.RemoveStaffAsync(removedStaff);
        var path = $"/v1/businesses/{shop.BusinessId}/bookings";

        Assert.Equal([past], Ids(await OkAsync(shop.OwnerId, $"{path}?staffId={removedStaff}")));
        await Json.ErrorAsync(await Get(shop.OwnerId, $"{path}?staffId={elsewhere.StaffA}"), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Get(shop.OwnerId, $"{path}?staffId={Guid.NewGuid()}"), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Staff_list_only_their_own_bookings_and_another_staffId_is_403()
    {
        var shop = await ShopAsync();
        var mine = await BookingAsync(shop, shop.StaffA, Base.AddDays(1));
        var mineToo = await BookingAsync(shop, shop.StaffA, Base);
        await BookingAsync(shop, shop.StaffB, Base.AddHours(3));
        var path = $"/v1/businesses/{shop.BusinessId}/bookings";

        Assert.Equal([mineToo, mine], Ids(await OkAsync(shop.UserA, path)));
        Assert.Equal([mineToo, mine], Ids(await OkAsync(shop.UserA, $"{path}?staffId={shop.StaffA}")));
        await Json.ErrorAsync(await Get(shop.UserA, $"{path}?staffId={shop.StaffB}"), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await Get(shop.UserA, $"{path}?staffId={Guid.NewGuid()}"), HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task An_owner_who_is_also_staff_lists_everything()
    {
        var shop = await ShopAsync();
        var ownerStaff = await fixture.Sql.InsertStaffAsync(shop.BusinessId, shop.OwnerId);
        var own = await BookingAsync(shop, ownerStaff, Base);
        var other = await BookingAsync(shop, shop.StaffB, Base.AddDays(1));

        Assert.Equal([own, other], Ids(await OkAsync(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings")));
    }

    [Fact]
    public async Task Outsiders_and_an_unknown_business_get_404()
    {
        var shop = await ShopAsync();
        await BookingAsync(shop, shop.StaffA, Base);
        var removedUser = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.RemoveStaffAsync(await fixture.Sql.InsertStaffAsync(shop.BusinessId, removedUser));

        foreach (var caller in new[] { shop.CustomerId, removedUser, await fixture.Sql.InsertUserAsync(), await fixture.Sql.InsertAdminUserAsync() })
        {
            await Json.ErrorAsync(await Get(caller, $"/v1/businesses/{shop.BusinessId}/bookings"), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await Get(shop.OwnerId, $"/v1/businesses/{Guid.NewGuid()}/bookings"), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task The_business_list_pages_through_every_item_once()
    {
        var shop = await ShopAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 5; i++) expected.Add(await BookingAsync(shop, i % 2 == 0 ? shop.StaffA : shop.StaffB, Base.AddHours(i)));

        Assert.Equal(expected, await AllPagesAsync(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings", limit: 2));
    }

    // ------------------------------------------------------------ GET /v1/bookings/{bookingId}/history (ADR 0003)

    private async Task HistoryRowAsync(Guid bookingId, string type, Guid actor, string role, DateTime at, Guid staffId, DateTime start,
        Guid? previousStaff = null, DateTime? previousStart = null, bool overridden = false) =>
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO booking_history (id, booking_id, type, actor_user_id, actor_role, at, staff_id, start_utc, end_utc, " +
            "previous_staff_id, previous_start_utc, previous_end_utc, minimum_notice_overridden) VALUES (@id, @bookingId, @type, @actor, @role, @at, " +
            "@staffId, @start, @end, @previousStaff, @previousStart, @previousEnd, @overridden)",
            new
            {
                id = Guid.NewGuid(), bookingId, type, actor, role, at, staffId, start, end = start.AddHours(1), previousStaff, previousStart,
                previousEnd = previousStart?.AddHours(1), overridden,
            });

    /// <summary>A booking made by staff member A for the customer, then moved by the owner to staff B with the notice override.</summary>
    private async Task<Guid> HistoryBookingAsync(Shop shop)
    {
        var id = await BookingAsync(shop, shop.StaffB, Base.AddDays(1));
        await HistoryRowAsync(id, "Created", shop.UserA, "Staff", Base.AddDays(-3), shop.StaffA, Base);
        await HistoryRowAsync(id, "Rescheduled", shop.OwnerId, "Owner", Base.AddDays(-2), shop.StaffB, Base.AddDays(1),
            previousStaff: shop.StaffA, previousStart: Base, overridden: true);
        return id;
    }

    [Fact]
    public async Task The_customer_sees_the_history_oldest_first_without_actor_ids()
    {
        var shop = await ShopAsync();
        var id = await HistoryBookingAsync(shop);

        var page = await OkAsync(shop.CustomerId, $"/v1/bookings/{id}/history");

        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        var (created, moved) = (page.GetProperty("items")[0], page.GetProperty("items")[1]);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());

        Assert.Equal(["actor", "at", "minimumNoticeOverridden", "slot", "type"], created.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Created", created.GetProperty("type").GetString());
        Assert.Equal(Iso(Base.AddDays(-3)), created.GetProperty("at").GetString());
        Assert.Equal(["name", "role"], created.GetProperty("actor").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Staff", created.GetProperty("actor").GetProperty("role").GetString());
        Assert.Equal("Ann Staff", created.GetProperty("actor").GetProperty("name").GetString());
        Assert.False(created.GetProperty("minimumNoticeOverridden").GetBoolean());
        Assert.Equal(shop.StaffA, created.GetProperty("slot").GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(Base), created.GetProperty("slot").GetProperty("startUtc").GetString());
        Assert.Equal(Iso(Base.AddHours(1)), created.GetProperty("slot").GetProperty("endUtc").GetString());

        Assert.Equal("Rescheduled", moved.GetProperty("type").GetString());
        Assert.Equal("Owner", moved.GetProperty("actor").GetProperty("role").GetString());
        Assert.Equal("Olive Owner", moved.GetProperty("actor").GetProperty("name").GetString());
        Assert.False(moved.GetProperty("actor").TryGetProperty("userId", out _));
        Assert.True(moved.GetProperty("minimumNoticeOverridden").GetBoolean());
        Assert.Equal(shop.StaffB, moved.GetProperty("slot").GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(Base.AddDays(1)), moved.GetProperty("slot").GetProperty("startUtc").GetString());
        Assert.Equal(shop.StaffA, moved.GetProperty("previousSlot").GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(Base), moved.GetProperty("previousSlot").GetProperty("startUtc").GetString());
        Assert.Equal(Iso(Base.AddHours(1)), moved.GetProperty("previousSlot").GetProperty("endUtc").GetString());
    }

    [Fact]
    public async Task The_booking_staff_member_and_the_owner_also_see_actor_ids()
    {
        var shop = await ShopAsync();
        var id = await HistoryBookingAsync(shop);

        foreach (var caller in new[] { shop.UserB, shop.OwnerId })
        {
            var items = (await OkAsync(caller, $"/v1/bookings/{id}/history")).GetProperty("items");
            Assert.Equal(shop.UserA, items[0].GetProperty("actor").GetProperty("userId").GetGuid());
            Assert.Equal(shop.OwnerId, items[1].GetProperty("actor").GetProperty("userId").GetGuid());
        }
    }

    [Fact]
    public async Task A_customer_who_is_also_staff_elsewhere_sees_no_actor_ids()
    {
        var shop = await ShopAsync();
        var id = await HistoryBookingAsync(shop);
        var elsewhere = await ShopAsync();
        await fixture.Sql.InsertStaffAsync(elsewhere.BusinessId, shop.CustomerId);

        var items = (await OkAsync(shop.CustomerId, $"/v1/bookings/{id}/history")).GetProperty("items");

        Assert.False(items[0].GetProperty("actor").TryGetProperty("userId", out _));
    }

    [Fact]
    public async Task The_actor_name_is_the_current_name()
    {
        var shop = await ShopAsync();
        var id = await HistoryBookingAsync(shop);
        await fixture.Sql.ExecuteAsync("UPDATE users SET name = 'Ann Renamed' WHERE id = @id", new { id = shop.UserA });

        var items = (await OkAsync(shop.CustomerId, $"/v1/bookings/{id}/history")).GetProperty("items");

        Assert.Equal("Ann Renamed", items[0].GetProperty("actor").GetProperty("name").GetString());
    }

    [Fact]
    public async Task History_pages_and_is_404_for_anyone_who_cannot_view_the_booking()
    {
        var shop = await ShopAsync();
        var id = await HistoryBookingAsync(shop);

        var first = await OkAsync(shop.CustomerId, $"/v1/bookings/{id}/history?limit=1");
        Assert.Equal("Created", Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("type").GetString());
        var second = await OkAsync(shop.CustomerId,
            $"/v1/bookings/{id}/history?limit=1&cursor=" + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!));
        Assert.Equal("Rescheduled", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        // Staff member A made the booking but it is now staff B's: A can no longer view it.
        foreach (var caller in new[] { shop.UserA, await fixture.Sql.InsertUserAsync(), await fixture.Sql.InsertAdminUserAsync() })
        {
            await Json.ErrorAsync(await Get(caller, $"/v1/bookings/{id}/history"), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await Get(shop.CustomerId, $"/v1/bookings/{Guid.NewGuid()}/history"), HttpStatusCode.NotFound, "NotFound");
        var error = await Json.ErrorAsync(await Get(shop.CustomerId, $"/v1/bookings/{id}/history?cursor=junk"), HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("cursor", Json.FieldNames(error));
    }

    // ------------------------------------------------------------ Code review 2026-11-20

    [Fact]
    public async Task A_cursor_from_another_kind_of_list_is_refused()
    {
        // Paging.cs: a cursor must come from a previous page of the same kind of list. Booking lists, booking
        // history and time off are each their own kind (both booking lists share one kind: same items, same order).
        var shop = await ShopAsync();
        var bookingId = await HistoryBookingAsync(shop);
        await BookingAsync(shop, shop.StaffA, Base.AddDays(5));
        foreach (var start in new[] { Base, Base.AddHours(2) })
        {
            await fixture.Sql.ExecuteAsync(
                "INSERT INTO time_off (id, staff_id, start_utc, end_utc, created_at) VALUES (@id, @staff, @start, @end, @start)",
                new { id = Guid.NewGuid(), staff = shop.StaffA, start, end = start.AddHours(1) });
        }

        async Task<string> CursorAsync(Guid caller, string path) =>
            (await OkAsync(caller, $"{path}?limit=1")).GetProperty("nextCursor").GetString()!;
        var bookingCursor = await CursorAsync(shop.CustomerId, "/v1/me/bookings");
        var historyCursor = await CursorAsync(shop.CustomerId, $"/v1/bookings/{bookingId}/history");
        var timeOffCursor = await CursorAsync(shop.OwnerId, $"/v1/staff/{shop.StaffA}/time-off");

        async Task RefusedAsync(Guid caller, string path, string cursor)
        {
            var error = await Json.ErrorAsync(await Get(caller, $"{path}?cursor={Uri.EscapeDataString(cursor)}"), HttpStatusCode.BadRequest, "ValidationFailed");
            Assert.Contains("cursor", Json.FieldNames(error));
        }
        await RefusedAsync(shop.CustomerId, $"/v1/bookings/{bookingId}/history", bookingCursor);
        await RefusedAsync(shop.CustomerId, $"/v1/bookings/{bookingId}/history", timeOffCursor);
        await RefusedAsync(shop.CustomerId, "/v1/me/bookings", historyCursor);
        await RefusedAsync(shop.CustomerId, "/v1/me/bookings", timeOffCursor);
        await RefusedAsync(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings", timeOffCursor);
        await RefusedAsync(shop.OwnerId, $"/v1/staff/{shop.StaffA}/time-off", bookingCursor);
        await RefusedAsync(shop.OwnerId, $"/v1/staff/{shop.StaffA}/time-off", historyCursor);
        // Each list still accepts its own.
        await OkAsync(shop.OwnerId, $"/v1/staff/{shop.StaffA}/time-off?cursor={Uri.EscapeDataString(timeOffCursor)}");
        await OkAsync(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings?cursor={Uri.EscapeDataString(bookingCursor)}");
    }

    [Fact]
    public async Task History_entries_at_the_same_moment_page_by_id_each_once()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, shop.StaffA, Base);
        for (var i = 0; i < 3; i++)
        {
            await HistoryRowAsync(id, "Rescheduled", shop.OwnerId, "Owner", Base.AddDays(-1), shop.StaffA, Base.AddHours(i),
                previousStaff: shop.StaffA, previousStart: Base);
        }
        var expected = (await fixture.Sql.RowsAsync("SELECT id FROM booking_history WHERE booking_id = @id", new { id }))
            .Select(r => Convert.ToString(r["id"])!).Order(StringComparer.Ordinal).ToList();

        var starts = new List<string>();
        string? cursor = null;
        for (var pages = 0; pages < 5; pages++)
        {
            var page = await OkAsync(shop.CustomerId, $"/v1/bookings/{id}/history?limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            starts.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("slot").GetProperty("startUtc").GetString()!));
            if (page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null) break;
            cursor = page.GetProperty("nextCursor").GetString();
        }

        Assert.Equal(3, starts.Count);
        Assert.Equal(3, starts.Distinct().Count());
        // Ordered by id within the same moment: map each id back to its slot start.
        var startById = (await fixture.Sql.RowsAsync("SELECT id, start_utc FROM booking_history WHERE booking_id = @id", new { id }))
            .ToDictionary(r => Convert.ToString(r["id"])!, r => Iso((DateTime)r["start_utc"]!));
        Assert.Equal(expected.Select(e => startById[e]), starts);
    }

    [Fact]
    public async Task A_malformed_staffId_on_the_business_list_is_ValidationFailed()
    {
        var shop = await ShopAsync();

        var error = await Json.ErrorAsync(await Get(shop.OwnerId, $"/v1/businesses/{shop.BusinessId}/bookings?staffId=abc"),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("staffId", Json.FieldNames(error));
    }
}
