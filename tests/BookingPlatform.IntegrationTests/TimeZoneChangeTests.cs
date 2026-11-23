using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T22: POST /v1/businesses/{id}/time-zone-change and GET .../time-zone-changes (R2; story "Owner changes the
/// business's time zone"). Decisions 2026-11-23 (plan/tasks.md, T22): bookings keep their UTC times; Confirmed
/// bookings that have not started and fall outside availability in the new zone go through the confirm pattern
/// (rule R6) and are cancelled with AvailabilityChanged (their BookingCancelled carries the old zone); every other
/// such booking whose local start or end differs gets BookingLocalTimeChanged; the zone is stored canonically and
/// an alias of the current zone is a no-op; locks are owner row, business row, staff rows, changed booking rows.
/// The business starts in America/New_York; staff A and B are available 09:00-17:00 local every day; Chicago is
/// always an hour behind New York. "Day" is three days ahead. Local times are turned into UTC with the OS zone data.
/// </summary>
public class TimeZoneChangeTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private sealed record Shop(Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid CustomerId);

    private const string NewYork = "America/New_York";
    private const string Chicago = "America/Chicago";
    private const string Message =
        "This time zone change leaves {0} booking(s) outside availability; confirming cancels them and notifies everyone affected.";

    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static DateTime Local(string zone, DateOnly date, int hour, int minute = 0) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), TimeZoneInfo.FindSystemTimeZoneById(zone)), DateTimeKind.Utc);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync(string zone = NewYork, string from = "09:00", string to = "17:00")
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = @zone WHERE id = @business", new { business, zone });
        var service = await fixture.Sql.InsertServiceAsync(business);

        async Task<(Guid Staff, Guid User)> StaffAsync()
        {
            var user = await fixture.Sql.InsertUserAsync(name: "Sam Staff");
            var staff = await fixture.Sql.InsertStaffAsync(business, user);
            await fixture.Sql.AssignStaffToServiceAsync(staff, service);
            foreach (var day in Enum.GetNames<DayOfWeek>())
            {
                await fixture.Sql.ExecuteAsync(
                    "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staff, @day, @from, @to)",
                    new { id = Guid.NewGuid(), staff, day, from, to });
            }
            return (staff, user);
        }

        var a = await StaffAsync();
        var b = await StaffAsync();
        return new Shop(owner, business, service, a.Staff, a.User, b.Staff, await fixture.Sql.InsertUserAsync(name: "Cara Customer"));
    }

    /// <summary>A Confirmed booking with 15-minute buffers (version 1), an hour long unless stated.</summary>
    private async Task<Guid> BookingAsync(Shop shop, DateTime startUtc, Guid? staffId = null, bool cancelled = false, int minutes = 60)
    {
        var parameters = Sql.BookingParameters(new(shop.BusinessId, staffId ?? shop.StaffA, shop.ServiceId, shop.CustomerId));
        parameters["start"] = startUtc;
        parameters["end"] = startUtc.AddMinutes(minutes);
        parameters["blockedStart"] = startUtc.AddMinutes(-15);
        parameters["blockedEnd"] = startUtc.AddMinutes(minutes + 15);
        if (cancelled)
        {
            parameters["status"] = "Cancelled";
            parameters["cancelledAt"] = fixture.Now;
            parameters["cancelledBy"] = shop.CustomerId;
            parameters["reason"] = "CustomerCancelled";
        }
        await fixture.Sql.ExecuteAsync(Sql.InsertBookingSql, parameters);
        return (Guid)parameters["id"]!;
    }

    private Task<HttpResponseMessage> Change(Guid callerId, Guid businessId, string? zone, params Guid[] confirm)
    {
        var body = new Dictionary<string, object?> { ["timeZone"] = zone };
        if (confirm.Length > 0) body["confirm"] = new { affectedBookingIds = confirm };
        return fixture.Api.ClientFor(callerId).PostJsonAsync($"/v1/businesses/{businessId}/time-zone-change", body);
    }

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private async Task<string> ZoneAsync(Guid businessId) =>
        Convert.ToString(await fixture.Sql.ScalarAsync("SELECT time_zone FROM businesses WHERE id = @businessId", new { businessId }))!;

    private Task<long> ChangeCountAsync(Guid businessId) =>
        CountAsync("SELECT COUNT(*) FROM time_zone_changes WHERE business_id = @businessId", new { businessId });

    private async Task<string> StatusAsync(Guid id) => Convert.ToString(await fixture.Sql.ScalarAsync("SELECT status FROM bookings WHERE id = @id", new { id }))!;

    private Task<long> EventCountAsync(Guid bookingId, string type) => CountAsync(
        "SELECT COUNT(*) FROM outbox WHERE message_group_id = @id AND event_type = @type", new { id = bookingId.ToString(), type });

    private async Task<JsonElement> EventAsync(Guid bookingId, string type)
    {
        Assert.Equal(1, await EventCountAsync(bookingId, type));
        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE message_group_id = @id AND event_type = @type", new { id = bookingId.ToString(), type }))!;
        EventSchemas.AssertValid(payload);
        return JsonDocument.Parse(payload).RootElement.GetProperty("data");
    }

    private static async Task<List<Guid>> AffectedAsync(HttpResponseMessage response)
    {
        var error = await Json.ErrorAsync(response, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal("R6", error.GetProperty("rule").GetString());
        return error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray().Select(b => b.GetProperty("bookingId").GetGuid()).ToList();
    }

    private async Task AssertNothingChangedAsync(Shop shop, string zone, params Guid[] bookings)
    {
        Assert.Equal(zone, await ZoneAsync(shop.BusinessId));
        Assert.Equal(0, await ChangeCountAsync(shop.BusinessId));
        foreach (var id in bookings)
        {
            Assert.Equal("Confirmed", await StatusAsync(id));
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
        }
    }

    // ------------------------------------------------------------ The story

    [Fact]
    public async Task R2_the_zone_changes_bookings_keep_their_UTC_times_and_customers_of_moved_bookings_are_notified()
    {
        var shop = await ShopAsync();
        var start = Local(NewYork, Day, 10); // 09:00 in Chicago: still inside availability
        var id = await BookingAsync(shop, start);

        var body = await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago), HttpStatusCode.OK);

        Assert.Equal(Chicago, body.GetProperty("timeZone").GetString());
        Assert.Equal(shop.BusinessId, body.GetProperty("id").GetGuid());
        Assert.Equal(Chicago, await ZoneAsync(shop.BusinessId));
        var booking = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;
        Assert.Equal("Confirmed", booking["status"]);
        Assert.Equal(start, booking["start_utc"]);
        Assert.Equal(1, Convert.ToInt32(booking["version"]));

        var change = (await fixture.Sql.RowAsync("SELECT * FROM time_zone_changes WHERE business_id = @b", new { b = shop.BusinessId }))!;
        Assert.Equal(NewYork, change["old_time_zone"]);
        Assert.Equal(Chicago, change["new_time_zone"]);
        Assert.Equal(shop.OwnerId, Guid.Parse(Convert.ToString(change["changed_by"])!));
        Assert.Equal(fixture.Now, change["changed_at"]);

        var data = await EventAsync(id, "BookingLocalTimeChanged");
        Assert.Equal(id, data.GetProperty("bookingId").GetGuid());
        Assert.Equal(shop.BusinessId, data.GetProperty("businessId").GetGuid());
        Assert.Equal(shop.CustomerId, data.GetProperty("customerId").GetGuid());
        Assert.Equal(NewYork, data.GetProperty("oldTimeZone").GetString());
        Assert.Equal(Chicago, data.GetProperty("newTimeZone").GetString());
        Assert.Equal(Guid.Parse(Convert.ToString(change["id"])!), data.GetProperty("timeZoneChangeId").GetGuid());
        Assert.Equal(start, data.GetProperty("slot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(shop.StaffA, data.GetProperty("slot").GetProperty("staffId").GetGuid());
    }

    [Fact]
    public async Task R2_R6_bookings_left_outside_availability_are_warned_about_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var outside = await BookingAsync(shop, Local(NewYork, Day, 9)); // 08:00 in Chicago
        var inside = await BookingAsync(shop, Local(NewYork, Day, 11), staffId: shop.StaffB);

        var error = await Json.ErrorAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago), HttpStatusCode.Conflict, "ConfirmationRequired");

        Assert.Equal("R6", error.GetProperty("rule").GetString());
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Message, 1), error.GetProperty("message").GetString());
        var listed = Assert.Single(error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray());
        Assert.Equal(outside, listed.GetProperty("bookingId").GetGuid());
        Assert.Equal(shop.StaffA, listed.GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(Local(NewYork, Day, 9)), listed.GetProperty("startUtc").GetString());
        await AssertNothingChangedAsync(shop, NewYork, outside, inside);
    }

    [Fact]
    public async Task R2_R6_on_confirmation_the_zone_changes_outside_bookings_are_cancelled_and_the_rest_notified()
    {
        var shop = await ShopAsync();
        var outside = await BookingAsync(shop, Local(NewYork, Day, 9));
        var inside = await BookingAsync(shop, Local(NewYork, Day, 11), staffId: shop.StaffB);

        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago, outside), HttpStatusCode.OK);

        Assert.Equal(Chicago, await ZoneAsync(shop.BusinessId));
        Assert.Equal(1, await ChangeCountAsync(shop.BusinessId));
        var row = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id = outside }))!;
        Assert.Equal("Cancelled", row["status"]);
        Assert.Equal("AvailabilityChanged", row["cancellation_reason"]);
        Assert.Equal(shop.OwnerId, Guid.Parse(Convert.ToString(row["cancelled_by"])!));
        Assert.Equal(2, Convert.ToInt32(row["version"]));
        var cancelled = await EventAsync(outside, "BookingCancelled");
        Assert.Equal("AvailabilityChanged", cancelled.GetProperty("reason").GetString());
        Assert.Equal("Owner", cancelled.GetProperty("cancelledBy").GetProperty("role").GetString());
        Assert.Equal(NewYork, cancelled.GetProperty("businessTimeZone").GetString()); // the zone it was booked in (decided 2026-11-23)
        Assert.Equal(0, await EventCountAsync(outside, "BookingLocalTimeChanged"));
        await EventAsync(inside, "BookingLocalTimeChanged");
        Assert.Equal("Confirmed", await StatusAsync(inside));
    }

    // ------------------------------------------------------------ The current zone

    [Theory]
    [InlineData(NewYork, NewYork)]
    [InlineData(NewYork, "US/Eastern")]
    [InlineData("Europe/London", "GB")]
    public async Task R2_the_current_zone_or_an_alias_of_it_is_a_200_no_op(string current, string requested)
    {
        var shop = await ShopAsync(current);
        var id = await BookingAsync(shop, Local(current, Day, 10));

        var body = await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, requested), HttpStatusCode.OK);

        Assert.Equal(current, body.GetProperty("timeZone").GetString());
        await AssertNothingChangedAsync(shop, current, id);
    }

    [Fact]
    public async Task R2_a_zone_stored_before_canonical_names_is_compared_canonically()
    {
        // T22 review: businesses saved before zones were stored canonically (T13) may hold an alias such as UTC.
        var shop = await ShopAsync("UTC");

        var body = await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, "Etc/UTC"), HttpStatusCode.OK);

        Assert.Equal("UTC", body.GetProperty("timeZone").GetString());
        Assert.Equal(0, await ChangeCountAsync(shop.BusinessId));
    }

    [Fact]
    public async Task R2_an_alias_is_stored_under_its_canonical_name()
    {
        var shop = await ShopAsync();

        var body = await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, "US/Central"), HttpStatusCode.OK);

        Assert.Equal(Chicago, body.GetProperty("timeZone").GetString());
        Assert.Equal(Chicago, await ZoneAsync(shop.BusinessId));
        Assert.Equal(Chicago, await fixture.Sql.ScalarAsync("SELECT new_time_zone FROM time_zone_changes WHERE business_id = @b", new { b = shop.BusinessId }));
    }

    // ------------------------------------------------------------ Which bookings are affected or notified

    [Fact]
    public async Task R2_every_moved_booking_across_staff_is_notified_once()
    {
        // T22 review: the moved bookings are locked in one statement with one parameter each.
        var shop = await ShopAsync();
        var ids = new[]
        {
            await BookingAsync(shop, Local(NewYork, Day, 10)),
            await BookingAsync(shop, Local(NewYork, Day, 12), staffId: shop.StaffB),
            await BookingAsync(shop, Local(NewYork, Day.AddDays(1), 14)),
        };

        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago), HttpStatusCode.OK);

        foreach (var id in ids) await EventAsync(id, "BookingLocalTimeChanged");
    }

    [Fact]
    public async Task R2_a_zone_on_the_same_clock_changes_no_booking_and_notifies_nobody()
    {
        // Europe/Lisbon has kept Europe/London's offsets and DST rules since 1996.
        var shop = await ShopAsync("Europe/London");
        var id = await BookingAsync(shop, Local("Europe/London", Day, 10));

        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, "Europe/Lisbon"), HttpStatusCode.OK);

        Assert.Equal(1, await ChangeCountAsync(shop.BusinessId));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
    }

    [Fact]
    public async Task R2_a_booking_whose_local_end_alone_moves_is_notified()
    {
        // Decided 2026-11-23: start or end. On London's spring-forward night, 00:30-01:30 UTC is 00:30-02:30 in London
        // but 00:30-01:30 in Abidjan (UTC all year): same local start, different local end.
        var shop = await ShopAsync("Europe/London", from: "00:00", to: "23:59");
        var springForward = new DateOnly(DateTime.UtcNow.Year + 1, 3, 31);
        while (springForward.DayOfWeek != DayOfWeek.Sunday) springForward = springForward.AddDays(-1); // last Sunday of March
        var start = springForward.ToDateTime(new TimeOnly(0, 30), DateTimeKind.Utc);
        var id = await BookingAsync(shop, start);

        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, "Africa/Abidjan"), HttpStatusCode.OK);

        await EventAsync(id, "BookingLocalTimeChanged");
    }

    [Fact]
    public async Task R2_bookings_that_have_started_or_are_cancelled_are_neither_cancelled_nor_notified()
    {
        var shop = await ShopAsync(from: "00:00", to: "23:59");
        await fixture.Sql.ExecuteAsync("UPDATE availability_rules SET start_local = '09:00', end_local = '17:00' WHERE staff_id = @s", new { s = shop.StaffB });
        var past = await BookingAsync(shop, fixture.Now.AddDays(-2));
        var inProgress = await BookingAsync(shop, fixture.Now.AddMinutes(-30), staffId: shop.StaffB); // outside B's hours in Chicago or not: never affected
        var cancelled = await BookingAsync(shop, Local(NewYork, Day, 9), staffId: shop.StaffB, cancelled: true);

        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago), HttpStatusCode.OK);

        Assert.Equal("Confirmed", await StatusAsync(inProgress));
        foreach (var id in new[] { past, inProgress, cancelled })
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
    }

    [Fact]
    public async Task R2_R8_a_date_exception_is_read_in_the_new_zone()
    {
        var shop = await ShopAsync();
        var start = Local(NewYork, Day, 13); // 12:00 in Chicago, on Day there too
        var id = await BookingAsync(shop, start);
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staff, @date, '12:30', '17:00')",
            new { id = Guid.NewGuid(), staff = shop.StaffA, date = Day.ToDateTime(TimeOnly.MinValue) });

        Assert.Equal([id], await AffectedAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago)));
    }

    [Fact]
    public async Task R6_several_bookings_across_staff_are_listed_by_start_and_all_cancelled_on_confirmation()
    {
        var shop = await ShopAsync();
        var later = await BookingAsync(shop, Local(NewYork, Day.AddDays(7), 9), staffId: shop.StaffB);
        var sooner = await BookingAsync(shop, Local(NewYork, Day, 9));

        Assert.Equal([sooner, later], await AffectedAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago)));
        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago, later, sooner), HttpStatusCode.OK);

        Assert.Equal("Cancelled", await StatusAsync(sooner));
        Assert.Equal("Cancelled", await StatusAsync(later));
    }

    // ------------------------------------------------------------ The confirm pattern

    [Fact]
    public async Task R6_a_confirmation_that_no_longer_matches_is_asked_again_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var first = await BookingAsync(shop, Local(NewYork, Day, 9));
        var second = await BookingAsync(shop, Local(NewYork, Day.AddDays(1), 9));

        Assert.Equal([first, second], await AffectedAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago, first)));
        Assert.Equal([first, second], await AffectedAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago, first, second, Guid.NewGuid())));

        await AssertNothingChangedAsync(shop, NewYork, first, second);
    }

    [Fact]
    public async Task R6_a_confirmation_no_longer_needed_goes_ahead()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Local(NewYork, Day, 9), cancelled: true);

        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago, id), HttpStatusCode.OK);
        Assert.Equal(Chicago, await ZoneAsync(shop.BusinessId));
    }

    // ------------------------------------------------------------ Validation and access

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Eastern Standard Time")]
    [InlineData("america/chicago")]
    public async Task An_invalid_zone_is_400_naming_timeZone(string? zone)
    {
        var shop = await ShopAsync();

        var error = await Json.ErrorAsync(await Change(shop.OwnerId, shop.BusinessId, zone), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("timeZone", Json.FieldNames(error));
        await AssertNothingChangedAsync(shop, NewYork);
    }

    [Fact]
    public async Task An_unknown_business_is_404_and_only_the_owner_may_change_it()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Local(NewYork, Day, 9));
        var otherOwner = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertBusinessAsync(otherOwner);

        await Json.ErrorAsync(await Change(shop.OwnerId, Guid.NewGuid(), Chicago), HttpStatusCode.NotFound, "NotFound");
        foreach (var caller in new[] { shop.UserA, shop.CustomerId, otherOwner, await fixture.Sql.InsertAdminUserAsync() })
        {
            var response = await Change(caller, shop.BusinessId, Chicago);
            var text = await response.Content.ReadAsStringAsync();
            await Json.ErrorAsync(response, HttpStatusCode.Forbidden, "Forbidden");
            Assert.DoesNotContain(id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
        await AssertNothingChangedAsync(shop, NewYork, id);
    }

    // ------------------------------------------------------------ Locks (ADR 0002)

    [Fact]
    public async Task ADR0002_the_owner_row_is_taken_before_the_business_and_staff_rows()
    {
        var shop = await ShopAsync();
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockUserRowAsync(shop.OwnerId))
        {
            request = Change(shop.OwnerId, shop.BusinessId, Chicago);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM businesses WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.BusinessId });
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id IN (@a, @b) FOR UPDATE NOWAIT", new { a = shop.StaffA, b = shop.StaffB });
        }

        await Json.ReadAsync(await request, HttpStatusCode.OK);
    }

    [Fact]
    public async Task ADR0002_the_business_row_is_taken_before_the_staff_rows()
    {
        var shop = await ShopAsync();
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffB))
        {
            request = Change(shop.OwnerId, shop.BusinessId, Chicago);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                probe.ExecuteAsync("SELECT id FROM businesses WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.BusinessId }));
            Assert.Contains("NOWAIT", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        await Json.ReadAsync(await request, HttpStatusCode.OK);
    }

    [Fact]
    public async Task R6_the_affected_set_is_worked_out_again_under_the_staff_locks()
    {
        var shop = await ShopAsync();
        var shown = await BookingAsync(shop, Local(NewYork, Day, 9));
        var sneakedStart = Local(NewYork, Day.AddDays(1), 9);
        var sneaked = Guid.NewGuid();
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffB))
        {
            request = Change(shop.OwnerId, shop.BusinessId, Chicago, shown);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync(Sql.InsertBookingSql, new
            {
                id = sneaked, staff = shop.StaffB, service = shop.ServiceId, customer = shop.CustomerId, start = sneakedStart, end = sneakedStart.AddHours(1),
                blockedStart = sneakedStart.AddMinutes(-15), blockedEnd = sneakedStart.AddMinutes(75), status = "Confirmed", now = fixture.Now,
                cancelledAt = (DateTime?)null, cancelledBy = (Guid?)null, reason = (string?)null,
            });
            await held.CommitAsync();
        }

        Assert.Equal([shown, sneaked], await AffectedAsync(await request));
        await AssertNothingChangedAsync(shop, NewYork, shown);
    }

    [Fact]
    public async Task R2_a_booking_waiting_on_the_staff_lock_is_judged_in_the_new_zone_once_the_change_commits()
    {
        // T22 review: booking creation reads the business again under the staff lock (T14), so a request that waited
        // through a change sees the new zone. 09:00 in New York is 08:00 in Chicago, outside 09:00-17:00.
        var shop = await ShopAsync();
        var start = Local(NewYork, Day, 9);
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            request = fixture.Api.ClientFor(shop.CustomerId).PostJsonAsync("/v1/bookings",
                new { serviceId = shop.ServiceId, staffId = shop.StaffA, startUtc = Iso(start) });
            await fixture.Sql.WaitForLockWaitAsync("staff");
            // What a committed change does to the business, while holding the staff lock as it would.
            await held.ExecuteAsync("UPDATE businesses SET time_zone = @zone WHERE id = @id", new { zone = Chicago, id = shop.BusinessId });
            await held.CommitAsync();
        }

        await Json.ErrorAsync(await request, HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM bookings WHERE staff_id = @staff", new { staff = shop.StaffA }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task T21_every_booking_that_gets_BookingLocalTimeChanged_is_locked_before_the_event_is_written(int heldIndex)
    {
        // Outbox ordering relies on each event writer holding its group's row (specs/05-data-model.md). Three moved
        // bookings; the test holds a different one each time, so each must be locked.
        var shop = await ShopAsync();
        var moved = new[]
        {
            await BookingAsync(shop, Local(NewYork, Day, 10)),
            await BookingAsync(shop, Local(NewYork, Day, 12), staffId: shop.StaffB),
            await BookingAsync(shop, Local(NewYork, Day.AddDays(1), 14)),
        };
        var id = moved[heldIndex];
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.BeginTransactionAsync())
        {
            await held.ExecuteAsync("SELECT id FROM bookings WHERE id = @id FOR SHARE", new { id });
            request = Change(shop.OwnerId, shop.BusinessId, Chicago);
            var waiting = await fixture.Sql.WaitForLockWaitAsync("bookings");
            Assert.Contains("FOR UPDATE", waiting, StringComparison.OrdinalIgnoreCase);
        }

        await Json.ReadAsync(await request, HttpStatusCode.OK);
        await EventAsync(id, "BookingLocalTimeChanged");
    }

    // ------------------------------------------------------------ The audit log

    private Task<HttpResponseMessage> List(Guid callerId, Guid businessId, string query = "") =>
        fixture.Api.ClientFor(callerId).GetAsync($"/v1/businesses/{businessId}/time-zone-changes{query}");

    [Fact]
    public async Task R2_changes_are_listed_newest_first_with_their_id_and_paged()
    {
        var shop = await ShopAsync();
        await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, Chicago), HttpStatusCode.OK);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        try
        {
            await Json.ReadAsync(await Change(shop.OwnerId, shop.BusinessId, "America/Denver"), HttpStatusCode.OK);
        }
        finally
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(-1));
        }

        var first = await Json.ReadAsync(await List(shop.OwnerId, shop.BusinessId, "?limit=1"), HttpStatusCode.OK);
        var newest = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal(Chicago, newest.GetProperty("oldTimeZone").GetString());
        Assert.Equal("America/Denver", newest.GetProperty("newTimeZone").GetString());
        Assert.Equal(shop.BusinessId, newest.GetProperty("businessId").GetGuid());
        Assert.Equal(shop.OwnerId, newest.GetProperty("changedBy").GetGuid());
        Assert.Equal(Iso(fixture.Now.AddMinutes(1)), newest.GetProperty("changedAt").GetDateTime().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        var storedId = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM time_zone_changes WHERE business_id = @b AND new_time_zone = 'America/Denver'", new { b = shop.BusinessId }));
        Assert.Equal(Guid.Parse(storedId!), newest.GetProperty("id").GetGuid());

        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await Json.ReadAsync(await List(shop.OwnerId, shop.BusinessId, $"?limit=1&cursor={Uri.EscapeDataString(cursor!)}"), HttpStatusCode.OK);
        var older = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(NewYork, older.GetProperty("oldTimeZone").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task The_audit_log_is_for_the_owner_only_and_rejects_another_list_cursor()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await List(shop.OwnerId, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await List(shop.UserA, shop.BusinessId), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await List(shop.CustomerId, shop.BusinessId), HttpStatusCode.Forbidden, "Forbidden");

        // A cursor from the time-off list is not a time zone change cursor.
        await Json.ReadAsync(await fixture.Api.ClientFor(shop.OwnerId).PostJsonAsync($"/v1/staff/{shop.StaffA}/time-off",
            new { startUtc = Iso(Local(NewYork, Day, 1)), endUtc = Iso(Local(NewYork, Day, 2)) }), HttpStatusCode.Created);
        await Json.ReadAsync(await fixture.Api.ClientFor(shop.OwnerId).PostJsonAsync($"/v1/staff/{shop.StaffA}/time-off",
            new { startUtc = Iso(Local(NewYork, Day, 3)), endUtc = Iso(Local(NewYork, Day, 4)) }), HttpStatusCode.Created);
        var timeOff = await Json.ReadAsync(await fixture.Api.ClientFor(shop.OwnerId).GetAsync($"/v1/staff/{shop.StaffA}/time-off?limit=1"), HttpStatusCode.OK);
        var foreign = timeOff.GetProperty("nextCursor").GetString()!;
        var error = await Json.ErrorAsync(await List(shop.OwnerId, shop.BusinessId, $"?cursor={Uri.EscapeDataString(foreign)}"), HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.Contains("cursor", Json.FieldNames(error));
    }
}
