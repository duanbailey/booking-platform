using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T19: R6 on weekly availability and date exception changes, through the confirm pattern. The two R6 scenarios of
/// the story "Staff member has weekly recurring availability plus date exceptions". Decisions 2026-11-23
/// (plan/tasks.md, T19): Confirmed bookings that have not started, on the dates the change governs, whose
/// appointment no longer lies inside a single block (buffers and steps don't count) are affected; confirmed ones
/// are cancelled with reason AvailabilityChanged. The business is in UTC unless a test says otherwise; staff A and
/// B are available 09:00-12:00 every day; the service lasts 60 minutes with 15-minute buffers. "Day" is three days
/// ahead.
/// </summary>
public class AvailabilityR6Tests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private sealed record Shop(Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB, Guid CustomerId);

    private const string Message =
        "This change leaves {0} booking(s) outside the staff member's availability; confirming cancels them and notifies everyone affected.";

    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static DateTime On(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync(string timeZone = "UTC")
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = @timeZone WHERE id = @business", new { business, timeZone });
        var service = await fixture.Sql.InsertServiceAsync(business);

        async Task<(Guid Staff, Guid User)> StaffAsync()
        {
            var user = await fixture.Sql.InsertUserAsync(name: "Sam Staff");
            var staff = await fixture.Sql.InsertStaffAsync(business, user);
            await fixture.Sql.AssignStaffToServiceAsync(staff, service);
            foreach (var day in Enum.GetNames<DayOfWeek>())
            {
                await fixture.Sql.ExecuteAsync(
                    "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staff, @day, '09:00', '12:00')",
                    new { id = Guid.NewGuid(), staff, day });
            }
            return (staff, user);
        }

        var a = await StaffAsync();
        var b = await StaffAsync();
        return new Shop(owner, business, service, a.Staff, a.User, b.Staff, b.User, await fixture.Sql.InsertUserAsync(name: "Cara Customer"));
    }

    /// <summary>A Confirmed booking with 15-minute buffers (version 1), an hour long unless stated.</summary>
    private async Task<Guid> BookingAsync(Shop shop, DateTime start, Guid? staffId = null, bool cancelled = false, int minutes = 60)
    {
        var parameters = Sql.BookingParameters(new(shop.BusinessId, staffId ?? shop.StaffA, shop.ServiceId, shop.CustomerId));
        parameters["start"] = start;
        parameters["end"] = start.AddMinutes(minutes);
        parameters["blockedStart"] = start.AddMinutes(-15);
        parameters["blockedEnd"] = start.AddMinutes(minutes + 15);
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

    private static Dictionary<string, object?> WithConfirm(Dictionary<string, object?> body, Guid[] confirm)
    {
        if (confirm.Length > 0) body["confirm"] = new { affectedBookingIds = confirm };
        return body;
    }

    /// <summary>PUT weekly availability with the same blocks on every day.</summary>
    private Task<HttpResponseMessage> PutEveryDay(Guid callerId, Guid staffId, (string Start, string End)[] blocks, params Guid[] confirm)
    {
        var week = Enum.GetNames<DayOfWeek>().SelectMany(day => blocks.Select(b => new { dayOfWeek = day, startLocal = b.Start, endLocal = b.End }));
        return fixture.Api.ClientFor(callerId).PutJsonAsync($"/v1/staff/{staffId}/weekly-availability",
            WithConfirm(new() { ["blocks"] = week.ToList() }, confirm));
    }

    private Task<HttpResponseMessage> PutRanges(Guid callerId, Guid staffId, DateOnly date, (string Start, string End)[] ranges, params Guid[] confirm) =>
        fixture.Api.ClientFor(callerId).PutJsonAsync($"/v1/staff/{staffId}/date-exceptions/{Date(date)}",
            WithConfirm(new() { ["ranges"] = ranges.Select(r => new { startLocal = r.Start, endLocal = r.End }).ToList() }, confirm));

    private Task<HttpResponseMessage> PutUnavailable(Guid callerId, Guid staffId, DateOnly date, params Guid[] confirm) =>
        fixture.Api.ClientFor(callerId).PutJsonAsync($"/v1/staff/{staffId}/date-exceptions/{Date(date)}",
            WithConfirm(new() { ["unavailable"] = true }, confirm));

    private Task<HttpResponseMessage> DeleteException(Guid callerId, Guid staffId, DateOnly date, params Guid[] confirm) =>
        DeleteRaw(callerId, staffId, date, confirm.Length == 0 ? "" : "?confirmAffectedBookingIds=" + string.Join(",", confirm));

    private Task<HttpResponseMessage> DeleteRaw(Guid callerId, Guid staffId, DateOnly date, string query) =>
        fixture.Api.ClientFor(callerId).DeleteAsync($"/v1/staff/{staffId}/date-exceptions/{Date(date)}{query}");

    private Task InsertExceptionAsync(Guid staffId, DateOnly date, string? start, string? end) => fixture.Sql.ExecuteAsync(
        "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staffId, @date, @start, @end)",
        new { id = Guid.NewGuid(), staffId, date = date.ToDateTime(TimeOnly.MinValue), start, end });

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    /// <summary>The staff member's weekly rows as "HH:mm-HH:mm" per row, sorted and de-duplicated across days.</summary>
    private async Task<string> StoredWeekAsync(Guid staffId) => Convert.ToString(await fixture.Sql.ScalarAsync(
        "SELECT IFNULL(GROUP_CONCAT(DISTINCT CONCAT(TIME_FORMAT(start_local, '%H:%i'), '-', TIME_FORMAT(end_local, '%H:%i')) " +
        "ORDER BY 1 SEPARATOR ', '), '') FROM availability_rules WHERE staff_id = @staffId", new { staffId }))!;

    private async Task<string> StoredDateAsync(Guid staffId, DateOnly date) => Convert.ToString(await fixture.Sql.ScalarAsync(
        "SELECT IFNULL(GROUP_CONCAT(IF(start_local IS NULL, 'unavailable', CONCAT(TIME_FORMAT(start_local, '%H:%i'), '-', TIME_FORMAT(end_local, '%H:%i'))) " +
        "ORDER BY start_local SEPARATOR ', '), '') FROM availability_exceptions WHERE staff_id = @staffId AND date = @date",
        new { staffId, date = date.ToDateTime(TimeOnly.MinValue) }))!;

    private async Task<string> StatusAsync(Guid id) => Convert.ToString(await fixture.Sql.ScalarAsync("SELECT status FROM bookings WHERE id = @id", new { id }))!;

    private static async Task<List<Guid>> AffectedAsync(HttpResponseMessage response)
    {
        var error = await Json.ErrorAsync(response, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal("R6", error.GetProperty("rule").GetString());
        return error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray().Select(b => b.GetProperty("bookingId").GetGuid()).ToList();
    }

    private async Task<JsonElement> CancelledEventAsync(Guid id)
    {
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id AND event_type = 'BookingCancelled'", new { id = id.ToString() }));
        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE message_group_id = @id AND event_type = 'BookingCancelled'", new { id = id.ToString() }))!;
        EventSchemas.AssertValid(payload);
        return JsonDocument.Parse(payload).RootElement.GetProperty("data");
    }

    private async Task AssertCancelledAsync(Guid id, Guid by, string role)
    {
        var row = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;
        Assert.Equal("Cancelled", row["status"]);
        Assert.Equal("AvailabilityChanged", row["cancellation_reason"]);
        Assert.Equal(by, Guid.Parse(Convert.ToString(row["cancelled_by"])!));
        Assert.Equal(fixture.Now, (DateTime)row["cancelled_at"]!);
        Assert.Equal(2, Convert.ToInt32(row["version"]));
        var data = await CancelledEventAsync(id);
        Assert.Equal("AvailabilityChanged", data.GetProperty("reason").GetString());
        Assert.Equal(2, data.GetProperty("bookingVersion").GetInt32());
        Assert.Equal(by, data.GetProperty("cancelledBy").GetProperty("userId").GetGuid());
        Assert.Equal(role, data.GetProperty("cancelledBy").GetProperty("role").GetString());
    }

    private async Task AssertUntouchedAsync(Guid id)
    {
        Assert.Equal("Confirmed", await StatusAsync(id));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
    }

    // ------------------------------------------------------------ Weekly availability (story, second R6 scenario)

    [Fact]
    public async Task R6_a_weekly_change_leaving_a_booking_outside_is_warned_about_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));

        var response = await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "10:30")]);

        var error = await Json.ErrorAsync(response, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal("R6", error.GetProperty("rule").GetString());
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Message, 1), error.GetProperty("message").GetString());
        var listed = Assert.Single(error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray());
        Assert.Equal(id, listed.GetProperty("bookingId").GetGuid());
        Assert.Equal(shop.StaffA, listed.GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(On(Day, 10)), listed.GetProperty("startUtc").GetString());
        Assert.Equal(Iso(On(Day, 11)), listed.GetProperty("endUtc").GetString());
        Assert.Equal("09:00-12:00", await StoredWeekAsync(shop.StaffA));
        await AssertUntouchedAsync(id);
    }

    [Fact]
    public async Task R6_on_confirmation_the_weekly_change_is_saved_and_the_bookings_cancelled_and_notified()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));

        var body = await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "10:30")], id), HttpStatusCode.OK);

        Assert.Equal(7, body.GetProperty("blocks").GetArrayLength());
        Assert.Equal("09:00-10:30", await StoredWeekAsync(shop.StaffA));
        await AssertCancelledAsync(id, shop.UserA, "Staff");
        var data = await CancelledEventAsync(id);
        Assert.Equal(shop.CustomerId, data.GetProperty("customerId").GetGuid());
        Assert.Equal(On(Day, 10), data.GetProperty("slot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task R6_the_owner_confirms_for_any_staff_member_as_Owner()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10), staffId: shop.StaffB);

        Assert.Equal([id], await AffectedAsync(await PutEveryDay(shop.OwnerId, shop.StaffB, [])));
        await Json.ReadAsync(await PutEveryDay(shop.OwnerId, shop.StaffB, [], id), HttpStatusCode.OK);

        await AssertCancelledAsync(id, shop.OwnerId, "Owner");
    }

    [Fact]
    public async Task R6_bookings_still_inside_on_other_staff_or_cancelled_are_not_affected()
    {
        var shop = await ShopAsync();
        var inside = await BookingAsync(shop, On(Day, 9));
        var otherStaff = await BookingAsync(shop, On(Day, 11), staffId: shop.StaffB);
        await BookingAsync(shop, On(Day, 11), cancelled: true);

        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "10:00")]), HttpStatusCode.OK);

        await AssertUntouchedAsync(inside);
        await AssertUntouchedAsync(otherStaff);
    }

    [Fact]
    public async Task R6_a_weekly_change_ignores_dates_with_a_date_exception()
    {
        // Decided 2026-11-23: the weekly availability doesn't govern a date with an exception.
        var shop = await ShopAsync();
        var onException = await BookingAsync(shop, On(Day, 10));
        await InsertExceptionAsync(shop.StaffA, Day, "09:00", "12:00");
        var nextDay = await BookingAsync(shop, On(Day.AddDays(1), 10));

        Assert.Equal([nextDay], await AffectedAsync(await PutEveryDay(shop.UserA, shop.StaffA, [])));
        await AssertUntouchedAsync(onException);
    }

    [Fact]
    public async Task R8_steps_do_not_count_a_booking_that_still_fits_stays()
    {
        // Decided 2026-11-23: 09:30 is no longer a step of 09:15-12:00 with 30-minute steps, but it still fits.
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 9, 30));

        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("09:15", "12:00")]), HttpStatusCode.OK);

        await AssertUntouchedAsync(id);
    }

    [Fact]
    public async Task R8_splitting_a_block_flags_a_booking_across_the_split_ADR0011()
    {
        // Touching blocks stay separate (ADR 0011): 11:30-12:30 no longer lies inside a single block.
        var shop = await ShopAsync();
        await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "17:00")]);
        var across = await BookingAsync(shop, On(Day, 11, 30));
        var inside = await BookingAsync(shop, On(Day, 13), staffId: shop.StaffA);

        Assert.Equal([across], await AffectedAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "12:00"), ("12:00", "17:00")])));
        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "12:00"), ("12:00", "17:00")], across), HttpStatusCode.OK);

        await AssertCancelledAsync(across, shop.UserA, "Staff");
        await AssertUntouchedAsync(inside);
    }

    [Fact]
    public async Task R8_availability_bounds_the_appointment_not_its_buffers()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10)); // blocks 09:45-11:15

        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("10:00", "11:00")]), HttpStatusCode.OK);

        await AssertUntouchedAsync(id);
    }

    [Fact]
    public async Task R6_bookings_that_have_started_are_never_affected()
    {
        // The API spec: bookings that have not started. A booking in progress or starting exactly now has started.
        var shop = await ShopAsync();
        await BookingAsync(shop, fixture.Now.AddDays(-2));
        await BookingAsync(shop, fixture.Now.AddMinutes(-30));
        await BookingAsync(shop, fixture.Now);
        var future = await BookingAsync(shop, fixture.Now.AddMinutes(1));

        Assert.Equal([future], await AffectedAsync(await PutEveryDay(shop.UserA, shop.StaffA, [])));
    }

    [Fact]
    public async Task R6_the_R4_window_does_not_apply_to_business_initiated_cancellations()
    {
        var shop = await ShopAsync();
        var soon = await BookingAsync(shop, fixture.Now.AddHours(2)); // inside the 24-hour window

        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [], soon), HttpStatusCode.OK);

        Assert.Equal("Cancelled", await StatusAsync(soon));
    }

    [Fact]
    public async Task R6_several_bookings_are_listed_by_start_and_all_cancelled_on_confirmation()
    {
        var shop = await ShopAsync();
        var later = await BookingAsync(shop, On(Day.AddDays(7), 10));
        var sooner = await BookingAsync(shop, On(Day, 11));

        Assert.Equal([sooner, later], await AffectedAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "10:00")])));
        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("09:00", "10:00")], later, sooner), HttpStatusCode.OK);

        await AssertCancelledAsync(sooner, shop.UserA, "Staff");
        await AssertCancelledAsync(later, shop.UserA, "Staff");
    }

    // ------------------------------------------------------------ Date exceptions (story, first R6 scenario)

    [Fact]
    public async Task R6_an_exception_with_different_hours_is_warned_about_then_saved_and_cancels_on_confirmation()
    {
        var shop = await ShopAsync();
        var outside = await BookingAsync(shop, On(Day, 9));
        var inside = await BookingAsync(shop, On(Day, 13), minutes: 30);
        var otherDay = await BookingAsync(shop, On(Day.AddDays(1), 9));

        Assert.Equal([outside], await AffectedAsync(await PutRanges(shop.UserA, shop.StaffA, Day, [("13:00", "15:00")])));
        Assert.Equal("", await StoredDateAsync(shop.StaffA, Day));
        await AssertUntouchedAsync(outside);

        var body = await Json.ReadAsync(await PutRanges(shop.UserA, shop.StaffA, Day, [("13:00", "15:00")], outside), HttpStatusCode.OK);

        Assert.False(body.GetProperty("unavailable").GetBoolean());
        Assert.Equal("13:00-15:00", await StoredDateAsync(shop.StaffA, Day));
        await AssertCancelledAsync(outside, shop.UserA, "Staff");
        await AssertUntouchedAsync(inside);
        await AssertUntouchedAsync(otherDay);
    }

    [Fact]
    public async Task R6_marking_a_date_unavailable_affects_every_booking_that_date_only()
    {
        var shop = await ShopAsync();
        var first = await BookingAsync(shop, On(Day, 9));
        var second = await BookingAsync(shop, On(Day, 11));
        var otherDay = await BookingAsync(shop, On(Day.AddDays(1), 9));

        var response = await PutUnavailable(shop.OwnerId, shop.StaffA, Day);
        var error = await Json.ErrorAsync(response, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Message, 2), error.GetProperty("message").GetString());

        await Json.ReadAsync(await PutUnavailable(shop.OwnerId, shop.StaffA, Day, second, first), HttpStatusCode.OK);

        Assert.Equal("unavailable", await StoredDateAsync(shop.StaffA, Day));
        await AssertCancelledAsync(first, shop.OwnerId, "Owner");
        await AssertCancelledAsync(second, shop.OwnerId, "Owner");
        await AssertUntouchedAsync(otherDay);
    }

    [Fact]
    public async Task R6_replacing_an_exception_checks_the_bookings_against_the_new_ranges()
    {
        var shop = await ShopAsync();
        await InsertExceptionAsync(shop.StaffA, Day, "14:00", "16:00");
        var id = await BookingAsync(shop, On(Day, 14));

        Assert.Equal([id], await AffectedAsync(await PutRanges(shop.UserA, shop.StaffA, Day, [("15:00", "16:00")])));
        Assert.Equal("14:00-16:00", await StoredDateAsync(shop.StaffA, Day));
        await Json.ReadAsync(await PutRanges(shop.UserA, shop.StaffA, Day, [("14:00", "15:00"), ("15:00", "16:00")]), HttpStatusCode.OK);
    }

    [Fact]
    public async Task R2_a_booking_belongs_to_the_business_local_date_of_its_start()
    {
        // 21:00 in New York is 01:00Z or 02:00Z the next UTC day.
        var shop = await ShopAsync("America/New_York");
        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [("20:00", "23:00")]), HttpStatusCode.OK);
        var start = TimeZoneInfo.ConvertTimeToUtc(Day.ToDateTime(new TimeOnly(21, 0)), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal(Day.AddDays(1), DateOnly.FromDateTime(start));
        var id = await BookingAsync(shop, start);

        await Json.ReadAsync(await PutUnavailable(shop.UserA, shop.StaffA, Day.AddDays(1)), HttpStatusCode.OK);
        Assert.Equal([id], await AffectedAsync(await PutUnavailable(shop.UserA, shop.StaffA, Day)));
    }

    // ------------------------------------------------------------ Removing a date exception

    [Fact]
    public async Task R6_removing_an_exception_checks_that_date_against_the_weekly_availability()
    {
        var shop = await ShopAsync();
        await InsertExceptionAsync(shop.StaffA, Day, "14:00", "16:00");
        var id = await BookingAsync(shop, On(Day, 14));
        var otherDay = await BookingAsync(shop, On(Day.AddDays(1), 10));

        Assert.Equal([id], await AffectedAsync(await DeleteException(shop.UserA, shop.StaffA, Day)));
        Assert.Equal("14:00-16:00", await StoredDateAsync(shop.StaffA, Day));
        await AssertUntouchedAsync(id);

        var response = await DeleteException(shop.UserA, shop.StaffA, Day, id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await StoredDateAsync(shop.StaffA, Day));
        await AssertCancelledAsync(id, shop.UserA, "Staff");
        await AssertUntouchedAsync(otherDay);
    }

    [Fact]
    public async Task R6_removing_an_exception_that_leaves_nothing_outside_needs_no_confirmation()
    {
        var shop = await ShopAsync();
        await InsertExceptionAsync(shop.StaffA, Day, null, null);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteException(shop.UserA, shop.StaffA, Day)).StatusCode);
    }

    [Fact]
    public async Task A_missing_exception_is_404_before_the_R6_check()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));

        await Json.ErrorAsync(await DeleteException(shop.UserA, shop.StaffA, Day, id), HttpStatusCode.NotFound, "NotFound");
        await AssertUntouchedAsync(id);
    }

    [Fact]
    public async Task Access_is_checked_before_the_confirmation_query()
    {
        // Code review 2026-11-23: an outsider gets 404 whatever the query holds (specs/07-nfr.md), a colleague 403.
        var shop = await ShopAsync();
        await InsertExceptionAsync(shop.StaffA, Day, null, null);
        var outsider = await fixture.Sql.InsertUserAsync();

        await Json.ErrorAsync(await DeleteRaw(outsider, shop.StaffA, Day, "?confirmAffectedBookingIds=nope"), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await DeleteRaw(shop.UserB, shop.StaffA, Day, "?confirmAffectedBookingIds=nope"), HttpStatusCode.Forbidden, "Forbidden");
        Assert.Equal("unavailable", await StoredDateAsync(shop.StaffA, Day));
    }

    [Theory]
    [InlineData(Change.Weekly)]
    [InlineData(Change.Exception)]
    [InlineData(Change.Delete)]
    public async Task Callers_who_may_not_make_the_change_never_see_the_affected_bookings(Change change)
    {
        // The 409 lists booking IDs and times, so access comes first: a colleague gets 403, an outsider 404.
        var shop = await ShopAsync();
        var id = await PrepareAsync(change, shop);
        var outsider = await fixture.Sql.InsertUserAsync();

        foreach (var (caller, status, code) in new[] { (shop.UserB, HttpStatusCode.Forbidden, "Forbidden"), (outsider, HttpStatusCode.NotFound, "NotFound") })
        {
            var response = await MakeChangeAs(caller, change, shop);
            var text = await response.Content.ReadAsStringAsync();
            await Json.ErrorAsync(response, status, code);
            Assert.DoesNotContain(id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
        await AssertUntouchedAsync(id);
    }

    [Fact]
    public async Task Spaces_around_the_confirmed_IDs_are_ignored()
    {
        // Code review 2026-11-23: "id1, id2" is a natural way to write the list.
        var shop = await ShopAsync();
        await InsertExceptionAsync(shop.StaffA, Day, "14:00", "16:00");
        var first = await BookingAsync(shop, On(Day, 14));
        var second = await BookingAsync(shop, On(Day, 15));

        var response = await DeleteRaw(shop.UserA, shop.StaffA, Day, $"?confirmAffectedBookingIds=%20{first},%20{second}%20");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertCancelledAsync(first, shop.UserA, "Staff");
        await AssertCancelledAsync(second, shop.UserA, "Staff");
    }

    [Theory]
    [InlineData("?confirmAffectedBookingIds=")]
    [InlineData("?confirmAffectedBookingIds=%20")]
    [InlineData("?confirmAffectedBookingIds=11111111-1111-1111-1111-111111111111,%20,22222222-2222-2222-2222-222222222222")]
    [InlineData("?confirmAffectedBookingIds=nope")]
    [InlineData("?confirmAffectedBookingIds=11111111-1111-1111-1111-111111111111,")]
    [InlineData("?confirmAffectedBookingIds=11111111-1111-1111-1111-111111111111,11111111-1111-1111-1111-111111111111")]
    [InlineData("?confirmAffectedBookingIds=11111111-1111-1111-1111-111111111111&confirmAffectedBookingIds=22222222-2222-2222-2222-222222222222")]
    public async Task A_malformed_confirmation_query_is_400_naming_it_and_nothing_changes(string query)
    {
        var shop = await ShopAsync();
        await InsertExceptionAsync(shop.StaffA, Day, null, null);

        var error = await Json.ErrorAsync(await DeleteRaw(shop.UserA, shop.StaffA, Day, query), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("confirmAffectedBookingIds", Json.FieldNames(error));
        Assert.Equal("unavailable", await StoredDateAsync(shop.StaffA, Day));
    }

    // ------------------------------------------------------------ The confirm pattern

    [Fact]
    public async Task R6_a_confirmation_that_no_longer_matches_is_asked_again_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var first = await BookingAsync(shop, On(Day, 9));
        var second = await BookingAsync(shop, On(Day, 11));

        Assert.Equal([first, second], await AffectedAsync(await PutUnavailable(shop.UserA, shop.StaffA, Day, first)));
        Assert.Equal([first, second], await AffectedAsync(await PutUnavailable(shop.UserA, shop.StaffA, Day, first, second, Guid.NewGuid())));

        Assert.Equal("", await StoredDateAsync(shop.StaffA, Day));
        await AssertUntouchedAsync(first);
    }

    [Fact]
    public async Task R6_a_booking_never_shown_is_never_cancelled()
    {
        var shop = await ShopAsync();
        var shown = await BookingAsync(shop, On(Day, 9));
        Assert.Equal([shown], await AffectedAsync(await PutEveryDay(shop.UserA, shop.StaffA, [])));
        var newer = await BookingAsync(shop, On(Day, 11)); // booked after the warning

        Assert.Equal([shown, newer], await AffectedAsync(await PutEveryDay(shop.UserA, shop.StaffA, [], shown)));
        await AssertUntouchedAsync(newer);
        await AssertUntouchedAsync(shown);
        Assert.Equal("09:00-12:00", await StoredWeekAsync(shop.StaffA));
    }

    [Fact]
    public async Task R6_a_confirmation_no_longer_needed_goes_ahead()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10), cancelled: true);
        await InsertExceptionAsync(shop.StaffA, Day.AddDays(1), "13:00", "14:00");

        await Json.ReadAsync(await PutEveryDay(shop.UserA, shop.StaffA, [], id), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteException(shop.UserA, shop.StaffA, Day.AddDays(1), id)).StatusCode);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
    }

    // ------------------------------------------------------------ Locks (ADR 0002)

    public enum Change { Weekly, Exception, Delete }

    private Task<HttpResponseMessage> MakeChange(Change change, Shop shop, params Guid[] confirm) => MakeChangeAs(shop.UserA, change, shop, confirm);

    private Task<HttpResponseMessage> MakeChangeAs(Guid callerId, Change change, Shop shop, params Guid[] confirm) => change switch
    {
        Change.Weekly => PutEveryDay(callerId, shop.StaffA, [], confirm),
        Change.Exception => PutUnavailable(callerId, shop.StaffA, Day, confirm),
        _ => DeleteException(callerId, shop.StaffA, Day, confirm),
    };

    private async Task<Guid> PrepareAsync(Change change, Shop shop)
    {
        if (change == Change.Delete)
        {
            await InsertExceptionAsync(shop.StaffA, Day, "14:00", "16:00");
            return await BookingAsync(shop, On(Day, 14));
        }
        return await BookingAsync(shop, On(Day, 9));
    }

    [Theory]
    [InlineData(Change.Weekly)]
    [InlineData(Change.Exception)]
    [InlineData(Change.Delete)]
    public async Task ADR0002_the_confirming_user_row_is_taken_before_the_staff_row(Change change)
    {
        var shop = await ShopAsync();
        var id = await PrepareAsync(change, shop);
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockUserRowAsync(shop.UserA))
        {
            request = MakeChange(change, shop, id);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.StaffA });
        }

        Assert.True((await request).IsSuccessStatusCode);
        Assert.Equal("Cancelled", await StatusAsync(id));
    }

    [Theory]
    [InlineData(Change.Weekly)]
    [InlineData(Change.Exception)]
    [InlineData(Change.Delete)]
    public async Task R6_the_affected_set_is_worked_out_again_under_the_staff_lock(Change change)
    {
        var shop = await ShopAsync();
        var shown = await PrepareAsync(change, shop);
        var sneakedStart = change == Change.Delete ? On(Day, 15) : On(Day, 11);
        Task<HttpResponseMessage> request;
        var sneaked = Guid.NewGuid();

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            request = MakeChange(change, shop, shown);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync(Sql.InsertBookingSql, new
            {
                id = sneaked, staff = shop.StaffA, service = shop.ServiceId, customer = shop.CustomerId, start = sneakedStart, end = sneakedStart.AddHours(1),
                blockedStart = sneakedStart.AddMinutes(-15), blockedEnd = sneakedStart.AddMinutes(75), status = "Confirmed", now = fixture.Now,
                cancelledAt = (DateTime?)null, cancelledBy = (Guid?)null, reason = (string?)null,
            });
            await held.CommitAsync();
        }

        Assert.Equal([shown, sneaked], await AffectedAsync(await request));
        await AssertUntouchedAsync(shown);
    }
}
