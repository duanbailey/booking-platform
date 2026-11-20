using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T18: R6 on POST /v1/staff/{staffId}/time-off, through the confirm pattern. Story "Staff or owner blocks time
/// off, which handles existing bookings". Decisions 2026-11-20 (plan/tasks.md, T18): bookings that have not
/// ended and whose blocked interval overlaps the time off are affected; confirmed ones are cancelled with reason
/// TimeOff. The business is in UTC; staff A and B are available 09:00-12:00 every day; the service lasts 60
/// minutes with 15-minute buffers. "Day" is three days ahead.
/// </summary>
public class TimeOffR6Tests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private sealed record Shop(Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB, Guid CustomerId);

    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static DateTime On(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @business", new { business });
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

    /// <summary>A Confirmed one-hour booking with 15-minute buffers (version 1).</summary>
    private async Task<Guid> BookingAsync(Shop shop, DateTime start, Guid? staffId = null, bool cancelled = false)
    {
        var parameters = Sql.BookingParameters(new(shop.BusinessId, staffId ?? shop.StaffA, shop.ServiceId, shop.CustomerId));
        parameters["start"] = start;
        parameters["end"] = start.AddHours(1);
        parameters["blockedStart"] = start.AddMinutes(-15);
        parameters["blockedEnd"] = start.AddMinutes(75);
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

    private Task<HttpResponseMessage> TimeOff(Guid callerId, Guid staffId, DateTime start, DateTime end, params Guid[] confirm)
    {
        var body = new Dictionary<string, object?> { ["startUtc"] = Iso(start), ["endUtc"] = Iso(end) };
        if (confirm.Length > 0) body["confirm"] = new { affectedBookingIds = confirm };
        return fixture.Api.ClientFor(callerId).PostJsonAsync($"/v1/staff/{staffId}/time-off", body);
    }

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<long> TimeOffCountAsync(Guid staffId) => CountAsync("SELECT COUNT(*) FROM time_off WHERE staff_id = @staffId", new { staffId });

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

    // ------------------------------------------------------------ The story

    [Fact]
    public async Task R6_time_off_overlapping_a_booking_buffers_included_is_warned_about_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10)); // blocks 09:45-11:15

        var response = await TimeOff(shop.UserA, shop.StaffA, On(Day, 11, 10), On(Day, 12)); // overlaps only the after-buffer

        var error = await Json.ErrorAsync(response, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal("R6", error.GetProperty("rule").GetString());
        // R6: cancellation notifications go to all affected users (customer and staff member, specs/06-events.md).
        Assert.Equal("This time off overlaps 1 booking(s); confirming cancels them and notifies everyone affected.",
            error.GetProperty("message").GetString());
        var listed = Assert.Single(error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray());
        Assert.Equal(id, listed.GetProperty("bookingId").GetGuid());
        Assert.Equal(shop.StaffA, listed.GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(On(Day, 10)), listed.GetProperty("startUtc").GetString());
        Assert.Equal(Iso(On(Day, 11)), listed.GetProperty("endUtc").GetString());
        Assert.Equal(0, await TimeOffCountAsync(shop.StaffA));
        Assert.Equal("Confirmed", await StatusAsync(id));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
    }

    [Fact]
    public async Task R6_on_confirmation_the_time_off_is_saved_the_bookings_cancelled_and_notified()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));

        var body = await Json.ReadAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 9), On(Day, 12), id), HttpStatusCode.Created);

        Assert.Equal(Iso(On(Day, 9)), body.GetProperty("startUtc").GetString());
        Assert.Equal(1, await TimeOffCountAsync(shop.StaffA));
        var row = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;
        Assert.Equal("Cancelled", row["status"]);
        Assert.Equal("TimeOff", row["cancellation_reason"]);
        Assert.Equal(shop.UserA, Guid.Parse(Convert.ToString(row["cancelled_by"])!));
        Assert.Equal(fixture.Now, (DateTime)row["cancelled_at"]!);
        Assert.Equal(2, Convert.ToInt32(row["version"]));
        var data = await CancelledEventAsync(id);
        Assert.Equal("TimeOff", data.GetProperty("reason").GetString());
        Assert.Equal(2, data.GetProperty("bookingVersion").GetInt32());
        Assert.Equal(shop.UserA, data.GetProperty("cancelledBy").GetProperty("userId").GetGuid());
        Assert.Equal("Staff", data.GetProperty("cancelledBy").GetProperty("role").GetString());
        Assert.Equal(shop.CustomerId, data.GetProperty("customerId").GetGuid());
        Assert.Equal(On(Day, 10), data.GetProperty("slot").GetProperty("startUtc").GetDateTime().ToUniversalTime());

        // Slot generation no longer offers slots during the time off.
        var slots = await Json.ReadAsync(await fixture.Api.ClientFor(shop.CustomerId)
            .GetAsync($"/v1/services/{shop.ServiceId}/slots?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}&staffId={shop.StaffA}"), HttpStatusCode.OK);
        Assert.Empty(slots.GetProperty("slots").EnumerateArray());
    }

    [Fact]
    public async Task R6_the_owner_confirms_for_any_staff_member_even_a_removed_one_as_Owner()
    {
        var shop = await ShopAsync();
        var a = await BookingAsync(shop, On(Day, 10));
        var b = await BookingAsync(shop, On(Day, 10), staffId: shop.StaffB);
        await fixture.Sql.RemoveStaffAsync(shop.StaffB);

        await Json.ReadAsync(await TimeOff(shop.OwnerId, shop.StaffA, On(Day, 9), On(Day, 12), a), HttpStatusCode.Created);
        Assert.Equal([b], await AffectedAsync(await TimeOff(shop.OwnerId, shop.StaffB, On(Day, 9), On(Day, 12))));
        await Json.ReadAsync(await TimeOff(shop.OwnerId, shop.StaffB, On(Day, 9), On(Day, 12), b), HttpStatusCode.Created);

        Assert.Equal("Owner", (await CancelledEventAsync(a)).GetProperty("cancelledBy").GetProperty("role").GetString());
        Assert.Equal("Owner", (await CancelledEventAsync(b)).GetProperty("cancelledBy").GetProperty("role").GetString());
    }

    [Fact]
    public async Task R6_the_R4_window_does_not_apply_to_business_initiated_cancellations()
    {
        var shop = await ShopAsync();
        var soon = await BookingAsync(shop, fixture.Now.AddHours(2)); // inside the 24-hour window

        await Json.ReadAsync(await TimeOff(shop.UserA, shop.StaffA, fixture.Now.AddHours(1), fixture.Now.AddHours(5), soon), HttpStatusCode.Created);

        Assert.Equal("Cancelled", await StatusAsync(soon));
    }

    // ------------------------------------------------------------ Which bookings are affected

    [Fact]
    public async Task R1_R6_touching_blocked_intervals_other_staff_and_cancelled_bookings_are_not_affected()
    {
        var shop = await ShopAsync();
        await BookingAsync(shop, On(Day, 10)); // blocks 09:45-11:15
        await BookingAsync(shop, On(Day, 11, 30), staffId: shop.StaffB);
        await BookingAsync(shop, On(Day, 11, 30), cancelled: true);

        await Json.ReadAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 11, 15), On(Day, 13)), HttpStatusCode.Created);
        await Json.ReadAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 8), On(Day, 9, 45)), HttpStatusCode.Created);
    }

    [Fact]
    public async Task R6_bookings_that_have_ended_are_never_affected_but_one_in_progress_is()
    {
        // Decided 2026-11-20: recording past time off never cancels appointments that already happened.
        var shop = await ShopAsync();
        await BookingAsync(shop, fixture.Now.AddDays(-2));
        await BookingAsync(shop, fixture.Now.AddHours(-1)); // ends exactly now: over
        var inProgress = await BookingAsync(shop, fixture.Now.AddMinutes(-30), staffId: shop.StaffB);

        await Json.ReadAsync(await TimeOff(shop.UserA, shop.StaffA, fixture.Now.AddDays(-3), fixture.Now.AddMinutes(1)), HttpStatusCode.Created);
        Assert.Equal([inProgress], await AffectedAsync(await TimeOff(shop.UserB, shop.StaffB, fixture.Now.AddHours(-1), fixture.Now.AddHours(1))));
    }

    [Fact]
    public async Task R6_several_bookings_are_listed_by_start_and_all_cancelled_on_confirmation()
    {
        var shop = await ShopAsync();
        var later = await BookingAsync(shop, On(Day.AddDays(1), 10));
        var sooner = await BookingAsync(shop, On(Day, 10));

        Assert.Equal([sooner, later], await AffectedAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 0), On(Day.AddDays(2), 0))));
        await Json.ReadAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 0), On(Day.AddDays(2), 0), later, sooner), HttpStatusCode.Created);

        foreach (var id in new[] { sooner, later })
        {
            Assert.Equal("Cancelled", await StatusAsync(id));
            var row = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;
            Assert.Equal(2, Convert.ToInt32(row["version"]));
            Assert.Equal("TimeOff", row["cancellation_reason"]);
            Assert.Equal(shop.UserA, Guid.Parse(Convert.ToString(row["cancelled_by"])!));
            var data = await CancelledEventAsync(id);
            Assert.Equal(id, data.GetProperty("bookingId").GetGuid());
            Assert.Equal(2, data.GetProperty("bookingVersion").GetInt32());
        }
    }

    // ------------------------------------------------------------ The confirm pattern

    [Fact]
    public async Task R6_a_confirmation_that_no_longer_matches_is_asked_again_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var first = await BookingAsync(shop, On(Day, 9));
        var second = await BookingAsync(shop, On(Day, 11));

        Assert.Equal([first, second], await AffectedAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 8), On(Day, 13), first)));
        Assert.Equal([first, second], await AffectedAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 8), On(Day, 13), first, second, Guid.NewGuid())));

        Assert.Equal(0, await TimeOffCountAsync(shop.StaffA));
        Assert.Equal("Confirmed", await StatusAsync(first));
    }

    [Fact]
    public async Task R6_a_booking_never_shown_is_never_cancelled()
    {
        var shop = await ShopAsync();
        var shown = await BookingAsync(shop, On(Day, 9));
        Assert.Equal([shown], await AffectedAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 8), On(Day, 13))));
        var newer = await BookingAsync(shop, On(Day, 11)); // booked after the warning

        Assert.Equal([shown, newer], await AffectedAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 8), On(Day, 13), shown)));
        Assert.Equal("Confirmed", await StatusAsync(newer));
        Assert.Equal("Confirmed", await StatusAsync(shown));
    }

    [Fact]
    public async Task R6_a_confirmation_no_longer_needed_goes_ahead()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));
        await fixture.Sql.ExecuteAsync(
            "UPDATE bookings SET status = 'Cancelled', cancelled_at = @now, cancelled_by = @by, cancellation_reason = 'CustomerCancelled' WHERE id = @id",
            new { now = fixture.Now, by = shop.CustomerId, id });

        await Json.ReadAsync(await TimeOff(shop.UserA, shop.StaffA, On(Day, 9), On(Day, 12), id), HttpStatusCode.Created);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
    }

    // ------------------------------------------------------------ Locks (ADR 0002)

    [Fact]
    public async Task ADR0002_the_confirming_user_row_is_taken_before_the_staff_row()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockUserRowAsync(shop.UserA))
        {
            request = TimeOff(shop.UserA, shop.StaffA, On(Day, 9), On(Day, 12), id);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.StaffA });
        }

        await Json.ReadAsync(await request, HttpStatusCode.Created);
    }

    [Fact]
    public async Task R6_the_affected_set_is_worked_out_again_under_the_staff_lock()
    {
        var shop = await ShopAsync();
        var shown = await BookingAsync(shop, On(Day, 9));
        Task<HttpResponseMessage> request;
        var sneaked = Guid.NewGuid();

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            request = TimeOff(shop.UserA, shop.StaffA, On(Day, 8), On(Day, 13), shown);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync(Sql.InsertBookingSql, new
            {
                id = sneaked, staff = shop.StaffA, service = shop.ServiceId, customer = shop.CustomerId, start = On(Day, 11), end = On(Day, 12),
                blockedStart = On(Day, 10, 45), blockedEnd = On(Day, 12, 15), status = "Confirmed", now = fixture.Now,
                cancelledAt = (DateTime?)null, cancelledBy = (Guid?)null, reason = (string?)null,
            });
            await held.CommitAsync();
        }

        Assert.Equal([shown, sneaked], await AffectedAsync(await request));
        Assert.Equal("Confirmed", await StatusAsync(shown));
    }
}
