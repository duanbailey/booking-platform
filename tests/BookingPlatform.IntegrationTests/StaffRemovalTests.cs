using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T20: POST /v1/staff/{staffId}/remove (story "Owner removes staff"; R6). Decisions 2026-11-23 (plan/tasks.md,
/// T20): the member's Confirmed bookings that have not started go through the confirm pattern and are cancelled
/// with reason StaffRemoved on confirmation, in the transaction that sets removed_at; removing a removed member is
/// a 200 no-op; the owner may remove their own staff row; owner only (404 only for an unknown staff member, else
/// 403). The business is in UTC; staff A and B are available 09:00-12:00 every day and perform the service (60
/// minutes, 15-minute buffers). "Day" is three days ahead.
/// </summary>
public class StaffRemovalTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private sealed record Shop(Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB, Guid CustomerId);

    private const string Message =
        "This staff member has {0} booking(s) that have not started; confirming removes them, cancels those bookings and notifies everyone affected.";

    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static DateTime On(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @business", new { business });
        var service = await fixture.Sql.InsertServiceAsync(business);

        async Task<(Guid Staff, Guid User)> StaffAsync(string name)
        {
            var user = await fixture.Sql.InsertUserAsync(name: name);
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

        var a = await StaffAsync("Sam Staff");
        var b = await StaffAsync("Bea Staff");
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

    private Task<HttpResponseMessage> Remove(Guid callerId, Guid staffId, params Guid[] confirm) =>
        confirm.Length == 0
            ? RemoveRaw(callerId, staffId, null)
            : fixture.Api.ClientFor(callerId).PostJsonAsync($"/v1/staff/{staffId}/remove", new { confirm = new { affectedBookingIds = confirm } });

    /// <summary>POST with exactly this body (null: no body and no Content-Type).</summary>
    private Task<HttpResponseMessage> RemoveRaw(Guid callerId, Guid staffId, string? json) =>
        fixture.Api.ClientFor(callerId).PostAsync($"/v1/staff/{staffId}/remove",
            json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private async Task<DateTime?> RemovedAtAsync(Guid staffId) =>
        await fixture.Sql.ScalarAsync("SELECT removed_at FROM staff WHERE id = @staffId", new { staffId }) is DateTime t ? t : null;

    private async Task<string> StatusAsync(Guid id) => Convert.ToString(await fixture.Sql.ScalarAsync("SELECT status FROM bookings WHERE id = @id", new { id }))!;

    private static async Task<List<Guid>> AffectedAsync(HttpResponseMessage response)
    {
        var error = await Json.ErrorAsync(response, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal("R6", error.GetProperty("rule").GetString());
        return error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray().Select(b => b.GetProperty("bookingId").GetGuid()).ToList();
    }

    private async Task AssertCancelledAsync(Guid id, Guid by)
    {
        var row = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;
        Assert.Equal("Cancelled", row["status"]);
        Assert.Equal("StaffRemoved", row["cancellation_reason"]);
        Assert.Equal(by, Guid.Parse(Convert.ToString(row["cancelled_by"])!));
        Assert.Equal(fixture.Now, (DateTime)row["cancelled_at"]!);
        Assert.Equal(2, Convert.ToInt32(row["version"]));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id AND event_type = 'BookingCancelled'", new { id = id.ToString() }));
        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE message_group_id = @id AND event_type = 'BookingCancelled'", new { id = id.ToString() }))!;
        EventSchemas.AssertValid(payload);
        var data = JsonDocument.Parse(payload).RootElement.GetProperty("data");
        Assert.Equal("StaffRemoved", data.GetProperty("reason").GetString());
        Assert.Equal(2, data.GetProperty("bookingVersion").GetInt32());
        Assert.Equal(by, data.GetProperty("cancelledBy").GetProperty("userId").GetGuid());
        Assert.Equal("Owner", data.GetProperty("cancelledBy").GetProperty("role").GetString());
    }

    private async Task AssertUntouchedAsync(Guid id)
    {
        Assert.Equal("Confirmed", await StatusAsync(id));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() }));
    }

    // ------------------------------------------------------------ The story

    [Fact]
    public async Task A_member_without_bookings_is_removed_can_no_longer_act_as_staff_and_gets_no_slots()
    {
        var shop = await ShopAsync();
        var client = fixture.Api.ClientFor(shop.CustomerId);
        var slotsPath = $"/v1/services/{shop.ServiceId}/slots?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}&staffId={shop.StaffA}";
        Assert.NotEmpty((await Json.ReadAsync(await client.GetAsync(slotsPath), HttpStatusCode.OK)).GetProperty("slots").EnumerateArray());

        var body = await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA), HttpStatusCode.OK);

        Assert.Equal(shop.StaffA, body.GetProperty("staffId").GetGuid());
        Assert.Equal(shop.BusinessId, body.GetProperty("businessId").GetGuid());
        Assert.Equal(shop.UserA, body.GetProperty("userId").GetGuid());
        Assert.Equal("Sam Staff", body.GetProperty("name").GetString());
        Assert.Equal(fixture.Now, body.GetProperty("removedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(fixture.Now, await RemovedAtAsync(shop.StaffA));
        // No longer listed, no longer acting as staff, no slots.
        var staff = await Json.ReadAsync(await client.GetAsync($"/v1/businesses/{shop.BusinessId}/staff"), HttpStatusCode.OK);
        Assert.Equal([shop.StaffB], staff.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("staffId").GetGuid()));
        await Json.ErrorAsync(await fixture.Api.ClientFor(shop.UserA).GetAsync($"/v1/staff/{shop.StaffA}/weekly-availability"), HttpStatusCode.NotFound, "NotFound");
        Assert.Empty((await Json.ReadAsync(await client.GetAsync(slotsPath), HttpStatusCode.OK)).GetProperty("slots").EnumerateArray());
        // No event for the removal itself (decided 2026-11-23).
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @user", new { user = shop.UserA.ToString() }));
    }

    [Fact]
    public async Task R6_a_member_with_bookings_that_have_not_started_is_warned_about_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));

        var error = await Json.ErrorAsync(await Remove(shop.OwnerId, shop.StaffA), HttpStatusCode.Conflict, "ConfirmationRequired");

        Assert.Equal("R6", error.GetProperty("rule").GetString());
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Message, 1), error.GetProperty("message").GetString());
        var listed = Assert.Single(error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray());
        Assert.Equal(id, listed.GetProperty("bookingId").GetGuid());
        Assert.Equal(shop.StaffA, listed.GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(On(Day, 10)), listed.GetProperty("startUtc").GetString());
        Assert.Equal(Iso(On(Day, 11)), listed.GetProperty("endUtc").GetString());
        Assert.Null(await RemovedAtAsync(shop.StaffA));
        await AssertUntouchedAsync(id);
    }

    [Fact]
    public async Task R6_on_confirmation_the_member_is_removed_and_the_bookings_cancelled_and_notified()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));

        var body = await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA, id), HttpStatusCode.OK);

        Assert.Equal(fixture.Now, body.GetProperty("removedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(fixture.Now, await RemovedAtAsync(shop.StaffA));
        await AssertCancelledAsync(id, shop.OwnerId);
    }

    [Fact]
    public async Task A_removed_member_added_again_is_staff_again_with_earlier_bookings_still_linked()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));
        await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA, id), HttpStatusCode.OK);
        var email = Convert.ToString(await fixture.Sql.ScalarAsync("SELECT email FROM users WHERE id = @id", new { id = shop.UserA }))!;

        var added = await Json.ReadAsync(await fixture.Api.ClientFor(shop.OwnerId)
            .PostJsonAsync($"/v1/businesses/{shop.BusinessId}/staff", new { name = "Sam Staff", email }), HttpStatusCode.Created);

        Assert.Equal(shop.StaffA, added.GetProperty("staffId").GetGuid());
        Assert.Null(await RemovedAtAsync(shop.StaffA));
        Assert.Equal(shop.StaffA, Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync("SELECT staff_id FROM bookings WHERE id = @id", new { id }))!));
        Assert.Equal("Cancelled", await StatusAsync(id)); // restoring doesn't bring cancelled bookings back
    }

    // ------------------------------------------------------------ Which bookings are affected

    [Fact]
    public async Task R6_bookings_that_have_started_on_other_staff_or_cancelled_are_not_affected()
    {
        var shop = await ShopAsync();
        var past = await BookingAsync(shop, fixture.Now.AddDays(-2));
        var inProgress = await BookingAsync(shop, fixture.Now.AddMinutes(-30));
        var startingNow = await BookingAsync(shop, fixture.Now); // has started
        await BookingAsync(shop, On(Day, 10), cancelled: true);
        var otherStaff = await BookingAsync(shop, On(Day, 10), staffId: shop.StaffB);

        await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA), HttpStatusCode.OK);

        // A booking in progress stays Confirmed with its removed staff member (decided 2026-11-23).
        foreach (var id in new[] { past, inProgress, startingNow, otherStaff }) await AssertUntouchedAsync(id);
    }

    [Fact]
    public async Task R6_every_booking_that_has_not_started_is_listed_by_start_however_far_ahead()
    {
        var shop = await ShopAsync();
        var farAhead = await BookingAsync(shop, On(Day.AddYears(2), 10)); // beyond the advance window
        var soon = await BookingAsync(shop, fixture.Now.AddHours(2));       // inside the 24-hour R4 window
        var mid = await BookingAsync(shop, On(Day, 10));

        Assert.Equal([soon, mid, farAhead], await AffectedAsync(await Remove(shop.OwnerId, shop.StaffA)));
        await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA, farAhead, mid, soon), HttpStatusCode.OK);

        // R6: business-initiated, so the R4 window doesn't apply.
        foreach (var id in new[] { soon, mid, farAhead }) await AssertCancelledAsync(id, shop.OwnerId);
    }

    [Fact]
    public async Task The_owner_may_remove_their_own_staff_row()
    {
        var shop = await ShopAsync();
        var ownStaff = await fixture.Sql.InsertStaffAsync(shop.BusinessId, shop.OwnerId);
        var id = await BookingAsync(shop, On(Day, 10), staffId: ownStaff);

        Assert.Equal([id], await AffectedAsync(await Remove(shop.OwnerId, ownStaff)));
        await Json.ReadAsync(await Remove(shop.OwnerId, ownStaff, id), HttpStatusCode.OK);

        Assert.Equal(fixture.Now, await RemovedAtAsync(ownStaff));
        await AssertCancelledAsync(id, shop.OwnerId);
        // Still the owner.
        await Json.ReadAsync(await fixture.Api.ClientFor(shop.OwnerId).GetAsync($"/v1/staff/{shop.StaffB}/weekly-availability"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_returned_removedAt_is_the_stored_one_to_the_microsecond()
    {
        // datetime(6) keeps microseconds; the response must not show ticks the database dropped (found with curl).
        var shop = await ShopAsync();
        fixture.Clock.Advance(TimeSpan.FromTicks(15)); // 1.5 µs past the frozen second
        try
        {
            var body = await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA), HttpStatusCode.OK);

            var stored = await RemovedAtAsync(shop.StaffA);
            Assert.Equal(fixture.Now.AddTicks(-5), stored);
            Assert.Equal(stored, body.GetProperty("removedAt").GetDateTime().ToUniversalTime());
        }
        finally
        {
            fixture.Clock.Advance(TimeSpan.FromTicks(-15));
        }
    }

    [Fact]
    public async Task Removing_a_removed_member_is_a_200_no_op()
    {
        var shop = await ShopAsync();
        var removedAt = fixture.Now.AddDays(-1);
        await fixture.Sql.ExecuteAsync("UPDATE staff SET removed_at = @removedAt WHERE id = @id", new { removedAt, id = shop.StaffA });

        // A stale confirmation doesn't matter either: there is nothing to confirm.
        var body = await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA, Guid.NewGuid()), HttpStatusCode.OK);

        Assert.Equal(removedAt, body.GetProperty("removedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(removedAt, await RemovedAtAsync(shop.StaffA));
    }

    // ------------------------------------------------------------ The confirm pattern

    [Fact]
    public async Task R6_a_confirmation_that_no_longer_matches_is_asked_again_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var first = await BookingAsync(shop, On(Day, 9));
        var second = await BookingAsync(shop, On(Day, 11));

        Assert.Equal([first, second], await AffectedAsync(await Remove(shop.OwnerId, shop.StaffA, first)));
        Assert.Equal([first, second], await AffectedAsync(await Remove(shop.OwnerId, shop.StaffA, first, second, Guid.NewGuid())));

        Assert.Null(await RemovedAtAsync(shop.StaffA));
        await AssertUntouchedAsync(first);
    }

    [Fact]
    public async Task R6_a_booking_never_shown_is_never_cancelled()
    {
        var shop = await ShopAsync();
        var shown = await BookingAsync(shop, On(Day, 9));
        Assert.Equal([shown], await AffectedAsync(await Remove(shop.OwnerId, shop.StaffA)));
        var newer = await BookingAsync(shop, On(Day, 11)); // booked after the warning

        Assert.Equal([shown, newer], await AffectedAsync(await Remove(shop.OwnerId, shop.StaffA, shown)));
        await AssertUntouchedAsync(newer);
        await AssertUntouchedAsync(shown);
        Assert.Null(await RemovedAtAsync(shop.StaffA));
    }

    [Fact]
    public async Task R6_a_confirmation_no_longer_needed_goes_ahead()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10), cancelled: true);

        await Json.ReadAsync(await Remove(shop.OwnerId, shop.StaffA, id), HttpStatusCode.OK);
        Assert.Equal(fixture.Now, await RemovedAtAsync(shop.StaffA));
    }

    // ------------------------------------------------------------ Request body

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    public async Task The_body_is_optional(string? json)
    {
        var shop = await ShopAsync();

        await Json.ReadAsync(await RemoveRaw(shop.OwnerId, shop.StaffA, json), HttpStatusCode.OK);
        Assert.Equal(fixture.Now, await RemovedAtAsync(shop.StaffA));
    }

    [Theory]
    [InlineData("""{"confirm":{"affectedBookingIds":[]}}""", "confirm")]
    [InlineData("""{"confirm":{}}""", "confirm")]
    [InlineData("""{"confirm":{"affectedBookingIds":["11111111-1111-1111-1111-111111111111","11111111-1111-1111-1111-111111111111"]}}""", "confirm")]
    [InlineData("""{"reason":"x"}""", "")]
    [InlineData("[]", "")]
    public async Task An_invalid_body_is_400_and_nothing_changes(string json, string field)
    {
        var shop = await ShopAsync();

        var error = await Json.ErrorAsync(await RemoveRaw(shop.OwnerId, shop.StaffA, json), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(Json.FieldNames(error), f => f.StartsWith(field, StringComparison.OrdinalIgnoreCase));
        Assert.Null(await RemovedAtAsync(shop.StaffA));
    }

    // ------------------------------------------------------------ Access (specs/07-nfr.md)

    [Fact]
    public async Task An_unknown_staff_member_is_404()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await Remove(shop.OwnerId, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Only_the_owner_may_remove_staff_and_nobody_else_sees_the_affected_bookings()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));
        var otherOwner = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertBusinessAsync(otherOwner);
        var admin = await fixture.Sql.InsertAdminUserAsync();

        foreach (var caller in new[] { shop.UserA, shop.UserB, shop.CustomerId, otherOwner, admin })
        {
            var response = await Remove(caller, shop.StaffA);
            var text = await response.Content.ReadAsStringAsync();
            await Json.ErrorAsync(response, HttpStatusCode.Forbidden, "Forbidden");
            Assert.DoesNotContain(id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(await RemovedAtAsync(shop.StaffA));
        await AssertUntouchedAsync(id);
    }

    // ------------------------------------------------------------ Locks (ADR 0002)

    [Fact]
    public async Task ADR0002_the_owner_row_is_taken_before_the_staff_row()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10));
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockUserRowAsync(shop.OwnerId))
        {
            request = Remove(shop.OwnerId, shop.StaffA, id);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.StaffA });
        }

        await Json.ReadAsync(await request, HttpStatusCode.OK);
        Assert.Equal("Cancelled", await StatusAsync(id));
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
            request = Remove(shop.OwnerId, shop.StaffA, shown);
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
        Assert.Null(await RemovedAtAsync(shop.StaffA));
    }

    /// <summary>
    /// Holds staff A's row, starts <paramref name="request"/>, waits until it is blocked on that row, then removes
    /// staff A in the holding transaction and commits, as a removal that won the lock would.
    /// </summary>
    private async Task<HttpResponseMessage> RemoveWhileWaitingAsync(Shop shop, Func<Task<HttpResponseMessage>> request)
    {
        Task<HttpResponseMessage> pending;
        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            pending = request();
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync("UPDATE staff SET removed_at = @now WHERE id = @id", new { now = fixture.Now, id = shop.StaffA });
            await held.CommitAsync();
        }
        return await pending;
    }

    [Fact]
    public async Task R6_a_booking_waiting_on_the_staff_lock_is_refused_once_the_removal_commits()
    {
        // The removal relies on booking creation reading removed_at under the staff lock (T14), so no Confirmed
        // future booking can be left with a removed staff member.
        var shop = await ShopAsync();
        var start = On(Day, 10);

        var response = await RemoveWhileWaitingAsync(shop, () => fixture.Api.ClientFor(shop.CustomerId).PostJsonAsync("/v1/bookings",
            new { serviceId = shop.ServiceId, staffId = shop.StaffA, startUtc = Iso(start) }));

        await Json.ErrorAsync(response, HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM bookings WHERE staff_id = @staff", new { staff = shop.StaffA }));
    }

    [Fact]
    public async Task R6_a_reschedule_to_the_member_waiting_on_the_staff_lock_is_refused_once_the_removal_commits()
    {
        // Likewise for a reschedule moving a booking to the member (T17 reads the target under its lock).
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 9), staffId: shop.StaffB);

        var response = await RemoveWhileWaitingAsync(shop, () => fixture.Api.ClientFor(shop.OwnerId).PostJsonAsync($"/v1/bookings/{id}/reschedule",
            new { startUtc = Iso(On(Day, 10)), staffId = shop.StaffA }));

        await Json.ErrorAsync(response, HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        var row = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;
        Assert.Equal(shop.StaffB, Guid.Parse(Convert.ToString(row["staff_id"])!));
        Assert.Equal(On(Day, 9), (DateTime)row["start_utc"]!);
    }

    [Fact]
    public async Task A_removal_committed_while_another_waited_makes_the_second_a_no_op()
    {
        var shop = await ShopAsync();
        Task<HttpResponseMessage> request;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            request = Remove(shop.OwnerId, shop.StaffA);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync("UPDATE staff SET removed_at = @at WHERE id = @id", new { at = fixture.Now.AddMinutes(-1), id = shop.StaffA });
            await held.CommitAsync();
        }

        var body = await Json.ReadAsync(await request, HttpStatusCode.OK);
        Assert.Equal(fixture.Now.AddMinutes(-1), body.GetProperty("removedAt").GetDateTime().ToUniversalTime());
    }
}
