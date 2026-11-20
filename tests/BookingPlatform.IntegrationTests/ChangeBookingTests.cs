using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T17: POST /v1/bookings/{id}/cancel and /reschedule. Story "Customer cancels or reschedules, subject to the
/// cancellation window"; the reschedule scenarios of "Owner or staff overrides the minimum notice period"; R3, R4
/// (inclusive window), R5, R8; ADR 0002 (locks), 0003 (same ID, history), 0004 (buffers re-snapshotted).
/// Decisions 2026-11-20 are in plan/tasks.md (T17). The business is in UTC with the default 24-hour window and
/// 2-hour notice; staff A and B are available 09:00-12:00 every day; the service lasts 60 minutes with 15-minute
/// buffers. "Far" bookings start four days ahead at 10:00 (outside the window), "near" ones a few hours from now.
/// </summary>
public class ChangeBookingTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private sealed record Shop(
        Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB, Guid CustomerId);

    /// <summary>Three days ahead: where reschedules move to.</summary>
    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static DateTime On(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private DateTime Far => On(Day.AddDays(1), 10);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @business", new { business });
        var service = await fixture.Sql.InsertServiceAsync(business);
        var a = await StaffAsync(business, service);
        var b = await StaffAsync(business, service);
        return new Shop(owner, business, service, a.Staff, a.User, b.Staff, b.User, await fixture.Sql.InsertUserAsync(name: "Cara Customer"));
    }

    private async Task<(Guid Staff, Guid User)> StaffAsync(Guid business, Guid service, Guid? userId = null)
    {
        var user = userId ?? await fixture.Sql.InsertUserAsync(name: "Sam Staff");
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

    /// <summary>Inserts a Confirmed one-hour booking with 15-minute buffers (version 1).</summary>
    private async Task<Guid> BookingAsync(Shop shop, DateTime start, Guid? staffId = null, Guid? customerId = null, bool cancelled = false)
    {
        var parameters = Sql.BookingParameters(new(shop.BusinessId, staffId ?? shop.StaffA, shop.ServiceId, customerId ?? shop.CustomerId));
        parameters["start"] = start;
        parameters["end"] = start.AddHours(1);
        parameters["blockedStart"] = start.AddMinutes(-15);
        parameters["blockedEnd"] = start.AddMinutes(75);
        if (cancelled)
        {
            parameters["status"] = "Cancelled";
            parameters["cancelledAt"] = fixture.Now;
            parameters["cancelledBy"] = shop.OwnerId;
            parameters["reason"] = "BusinessCancelled";
        }
        await fixture.Sql.ExecuteAsync(Sql.InsertBookingSql, parameters);
        return (Guid)parameters["id"]!;
    }

    private Task<HttpResponseMessage> Cancel(Guid callerId, Guid bookingId) =>
        fixture.Api.ClientFor(callerId).PostAsync($"/v1/bookings/{bookingId}/cancel", null);

    private Task<HttpResponseMessage> Reschedule(Guid callerId, Guid bookingId, DateTime start, Guid? staffId = null, bool? overrideNotice = null)
    {
        var body = new Dictionary<string, object?> { ["startUtc"] = Iso(start) };
        if (staffId is not null) body["staffId"] = staffId;
        if (overrideNotice is not null) body["overrideMinimumNotice"] = overrideNotice;
        return fixture.Api.ClientFor(callerId).PostJsonAsync($"/v1/bookings/{bookingId}/reschedule", body);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request) => await Json.ReadAsync(await request, HttpStatusCode.OK);

    private async Task<Dictionary<string, object?>> RowAsync(Guid id) =>
        (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<long> HistoryCountAsync(Guid id) => CountAsync("SELECT COUNT(*) FROM booking_history WHERE booking_id = @id", new { id });

    private Task<long> EventCountAsync(Guid id) => CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = id.ToString() });

    /// <summary>The booking's only event of this type, validated against its schema.</summary>
    private async Task<JsonElement> EventAsync(Guid id, string type)
    {
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id AND event_type = @type", new { id = id.ToString(), type }));
        var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT payload FROM outbox WHERE message_group_id = @id AND event_type = @type", new { id = id.ToString(), type }))!;
        EventSchemas.AssertValid(payload);
        return JsonDocument.Parse(payload).RootElement.GetProperty("data");
    }

    /// <summary>A refused change leaves the booking as it was: still version 1, no history, no event.</summary>
    private async Task AssertUnchangedAsync(Guid id, DateTime start, Guid staffId, string status = "Confirmed")
    {
        var row = await RowAsync(id);
        Assert.Equal(status, row["status"]);
        Assert.Equal(start, (DateTime)row["start_utc"]!);
        Assert.Equal(staffId, Guid.Parse(Convert.ToString(row["staff_id"])!));
        Assert.Equal(1, Convert.ToInt32(row["version"]));
        Assert.Equal(0, await HistoryCountAsync(id));
        Assert.Equal(0, await EventCountAsync(id));
    }

    private async Task<List<string>> OfferedStartsAsync(Shop shop, DateOnly date, Guid staffId)
    {
        var body = await Json.ReadAsync(await fixture.Api.ClientFor(shop.CustomerId)
            .GetAsync($"/v1/services/{shop.ServiceId}/slots?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}&staffId={staffId}"), HttpStatusCode.OK);
        return body.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("startUtc").GetString()!).ToList();
    }

    // ================================================================= Cancel

    [Fact]
    public async Task R4_the_customer_cancels_outside_the_window_and_the_time_is_offered_again()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        Assert.DoesNotContain(Iso(Far), await OfferedStartsAsync(shop, Day.AddDays(1), shop.StaffA));

        var body = await OkAsync(Cancel(shop.CustomerId, id));

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(Iso(fixture.Now), body.GetProperty("cancelledAt").GetString());
        Assert.Equal("CustomerCancelled", body.GetProperty("cancellationReason").GetString());
        var row = await RowAsync(id);
        Assert.Equal(shop.CustomerId, Guid.Parse(Convert.ToString(row["cancelled_by"])!));
        Assert.Equal(2, Convert.ToInt32(row["version"]));
        Assert.Equal(0, await HistoryCountAsync(id)); // history records creation and moves only
        var data = await EventAsync(id, "BookingCancelled");
        Assert.Equal(2, data.GetProperty("bookingVersion").GetInt32());
        Assert.Equal(shop.BusinessId, data.GetProperty("businessId").GetGuid());
        Assert.Equal("UTC", data.GetProperty("businessTimeZone").GetString());
        Assert.Equal(shop.ServiceId, data.GetProperty("serviceId").GetGuid());
        Assert.Equal(shop.CustomerId, data.GetProperty("customerId").GetGuid());
        Assert.Equal(shop.StaffA, data.GetProperty("slot").GetProperty("staffId").GetGuid());
        Assert.Equal(Far, data.GetProperty("slot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(shop.CustomerId, data.GetProperty("cancelledBy").GetProperty("userId").GetGuid());
        Assert.Equal("Customer", data.GetProperty("cancelledBy").GetProperty("role").GetString());
        Assert.Equal("CustomerCancelled", data.GetProperty("reason").GetString());
        Assert.Contains(Iso(Far), await OfferedStartsAsync(shop, Day.AddDays(1), shop.StaffA));
    }

    [Fact]
    public async Task R4_the_customer_may_cancel_exactly_N_hours_before_but_not_a_second_later()
    {
        var shop = await ShopAsync();
        var exactly = await BookingAsync(shop, fixture.Now.AddHours(24));
        var inside = await BookingAsync(shop, fixture.Now.AddHours(24).AddSeconds(-1), staffId: shop.StaffB);

        await OkAsync(Cancel(shop.CustomerId, exactly));
        var error = await Json.ErrorAsync(await Cancel(shop.CustomerId, inside), HttpStatusCode.UnprocessableEntity, "CancellationWindowPassed");

        Assert.Equal("R4", error.GetProperty("rule").GetString());
        Assert.Equal(24, error.GetProperty("details").GetProperty("cancellationWindowHours").GetInt32());
        await AssertUnchangedAsync(inside, fixture.Now.AddHours(24).AddSeconds(-1), shop.StaffB);
    }

    [Fact]
    public async Task R4_the_booking_staff_member_and_the_owner_cancel_inside_the_window_as_the_business()
    {
        var shop = await ShopAsync();
        var byStaff = await BookingAsync(shop, fixture.Now.AddHours(3));
        var byOwner = await BookingAsync(shop, fixture.Now.AddHours(3), staffId: shop.StaffB);

        Assert.Equal("BusinessCancelled", (await OkAsync(Cancel(shop.UserA, byStaff))).GetProperty("cancellationReason").GetString());
        Assert.Equal("BusinessCancelled", (await OkAsync(Cancel(shop.OwnerId, byOwner))).GetProperty("cancellationReason").GetString());

        Assert.Equal("Staff", (await EventAsync(byStaff, "BookingCancelled")).GetProperty("cancelledBy").GetProperty("role").GetString());
        var owner = (await EventAsync(byOwner, "BookingCancelled")).GetProperty("cancelledBy");
        Assert.Equal("Owner", owner.GetProperty("role").GetString());
        Assert.Equal(shop.OwnerId, owner.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task R4_staff_and_owners_may_cancel_a_booking_that_has_started_or_ended()
    {
        var shop = await ShopAsync();
        var started = await BookingAsync(shop, fixture.Now.AddMinutes(-30));
        var ended = await BookingAsync(shop, fixture.Now.AddDays(-2), staffId: shop.StaffB);

        await OkAsync(Cancel(shop.UserA, started));
        await OkAsync(Cancel(shop.OwnerId, ended));
        await Json.ErrorAsync(await Cancel(shop.CustomerId, await BookingAsync(shop, fixture.Now.AddDays(-3))),
            HttpStatusCode.UnprocessableEntity, "CancellationWindowPassed");
    }

    [Fact]
    public async Task R4_cancelling_a_cancelled_booking_is_BookingCancelled_for_everyone()
    {
        var shop = await ShopAsync();
        var far = await BookingAsync(shop, Far, cancelled: true);
        var near = await BookingAsync(shop, fixture.Now.AddHours(2), cancelled: true);

        foreach (var caller in new[] { shop.CustomerId, shop.UserA, shop.OwnerId })
        {
            await Json.ErrorAsync(await Cancel(caller, far), HttpStatusCode.UnprocessableEntity, "BookingCancelled");
        }
        // Checked before the window.
        await Json.ErrorAsync(await Cancel(shop.CustomerId, near), HttpStatusCode.UnprocessableEntity, "BookingCancelled");
        Assert.Equal(0, await EventCountAsync(far));
    }

    [Fact]
    public async Task R4_anyone_who_cannot_view_the_booking_gets_404_on_cancel()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        var removedUser = await fixture.Sql.InsertUserAsync();
        var removed = (await StaffAsync(shop.BusinessId, shop.ServiceId, removedUser)).Staff;
        var removedBooking = await BookingAsync(shop, Far, staffId: removed);
        await fixture.Sql.RemoveStaffAsync(removed);

        foreach (var caller in new[] { shop.UserB, await fixture.Sql.InsertUserAsync(), await fixture.Sql.InsertAdminUserAsync() })
        {
            await Json.ErrorAsync(await Cancel(caller, id), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await Cancel(removedUser, removedBooking), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Cancel(shop.CustomerId, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await fixture.Api.CreateClient().PostAsync($"/v1/bookings/{id}/cancel", null), HttpStatusCode.Unauthorized, "Unauthenticated");
        await AssertUnchangedAsync(id, Far, shop.StaffA);
    }

    [Fact]
    public async Task R4_a_customer_who_is_also_the_owner_or_staff_member_uses_the_privilege_only_inside_the_window()
    {
        // Decided 2026-11-20: never blocked by the window; inside it the business reason and role apply.
        var shop = await ShopAsync();
        var ownerFar = await BookingAsync(shop, Far, customerId: shop.OwnerId);
        var ownerNear = await BookingAsync(shop, fixture.Now.AddHours(3), customerId: shop.OwnerId);
        var staffNear = await BookingAsync(shop, fixture.Now.AddHours(3), staffId: shop.StaffB, customerId: shop.UserB);

        Assert.Equal("CustomerCancelled", (await OkAsync(Cancel(shop.OwnerId, ownerFar))).GetProperty("cancellationReason").GetString());
        Assert.Equal("BusinessCancelled", (await OkAsync(Cancel(shop.OwnerId, ownerNear))).GetProperty("cancellationReason").GetString());
        Assert.Equal("BusinessCancelled", (await OkAsync(Cancel(shop.UserB, staffNear))).GetProperty("cancellationReason").GetString());

        Assert.Equal("Customer", (await EventAsync(ownerFar, "BookingCancelled")).GetProperty("cancelledBy").GetProperty("role").GetString());
        Assert.Equal("Owner", (await EventAsync(ownerNear, "BookingCancelled")).GetProperty("cancelledBy").GetProperty("role").GetString());
        Assert.Equal("Staff", (await EventAsync(staffNear, "BookingCancelled")).GetProperty("cancelledBy").GetProperty("role").GetString());
    }

    [Fact]
    public async Task ADR0002_cancel_takes_the_user_rows_before_the_staff_row()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        Task<HttpResponseMessage> cancel;

        // A booking for this customer holds their row exclusively; the cancel waits for it, holding no staff lock.
        await using (var held = await fixture.Sql.LockUserRowAsync(shop.CustomerId))
        {
            cancel = Cancel(shop.OwnerId, id);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.StaffA });
        }
        await OkAsync(cancel);

        var other = await BookingAsync(shop, Far, staffId: shop.StaffB);
        await using (var held = await fixture.Sql.LockUserRowAsync(shop.UserB)) // the actor's row
        {
            cancel = Cancel(shop.UserB, other);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.StaffB });
        }
        await OkAsync(cancel);
    }

    // ================================================================= Reschedule

    [Fact]
    public async Task R5_the_customer_moves_their_booking_with_the_same_staff_member_keeping_its_id()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        // ADR 0004: the move re-snapshots buffers from the service as it is now.
        await fixture.Sql.ExecuteAsync("UPDATE services SET buffer_before_minutes = 30, buffer_after_minutes = 0 WHERE id = @id", new { id = shop.ServiceId });
        var to = On(Day, 11);

        var body = await OkAsync(Reschedule(shop.CustomerId, id, to));

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(Iso(to), body.GetProperty("startUtc").GetString());
        Assert.Equal(Iso(to.AddHours(1)), body.GetProperty("endUtc").GetString());
        Assert.Equal(shop.StaffA, body.GetProperty("staffId").GetGuid());
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        var row = await RowAsync(id);
        Assert.Equal(to.AddMinutes(-30), (DateTime)row["blocked_start_utc"]!);
        Assert.Equal(to.AddHours(1), (DateTime)row["blocked_end_utc"]!);
        Assert.Equal(2, Convert.ToInt32(row["version"]));

        var history = (await fixture.Sql.RowAsync("SELECT * FROM booking_history WHERE booking_id = @id", new { id }))!;
        Assert.Equal("Rescheduled", history["type"]);
        Assert.Equal(shop.CustomerId, Guid.Parse(Convert.ToString(history["actor_user_id"])!));
        Assert.Equal("Customer", history["actor_role"]);
        Assert.Equal(fixture.Now, (DateTime)history["at"]!);
        Assert.Equal(to, (DateTime)history["start_utc"]!);
        Assert.Equal(Far, (DateTime)history["previous_start_utc"]!);
        Assert.Equal(Far.AddHours(1), (DateTime)history["previous_end_utc"]!);
        Assert.Equal(shop.StaffA, Guid.Parse(Convert.ToString(history["previous_staff_id"])!));
        Assert.False(Convert.ToBoolean(history["minimum_notice_overridden"]));

        var data = await EventAsync(id, "BookingRescheduled");
        Assert.Equal(1, await EventCountAsync(id)); // never with BookingCancelled or BookingCreated (ADR 0003)
        Assert.Equal(2, data.GetProperty("bookingVersion").GetInt32());
        Assert.Equal(Far, data.GetProperty("oldSlot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(to, data.GetProperty("newSlot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(shop.StaffA, data.GetProperty("newSlot").GetProperty("staffId").GetGuid());
        Assert.Equal("Customer", data.GetProperty("rescheduledBy").GetProperty("role").GetString());
        Assert.Contains(Iso(Far), await OfferedStartsAsync(shop, Day.AddDays(1), shop.StaffA)); // the old time is free again
    }

    [Fact]
    public async Task R4_customers_reschedule_with_the_same_staff_member_only_and_never_override()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);

        await Json.ErrorAsync(await Reschedule(shop.CustomerId, id, On(Day, 10), staffId: shop.StaffB), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await Reschedule(shop.CustomerId, id, On(Day, 10), overrideNotice: true), HttpStatusCode.Forbidden, "Forbidden");
        await AssertUnchangedAsync(id, Far, shop.StaffA);
        await OkAsync(Reschedule(shop.CustomerId, id, On(Day, 10), staffId: shop.StaffA));
    }

    [Fact]
    public async Task R4_the_customer_cannot_reschedule_inside_the_window_but_may_exactly_N_hours_before()
    {
        var shop = await ShopAsync();
        var inside = await BookingAsync(shop, fixture.Now.AddHours(23));
        var exactly = await BookingAsync(shop, fixture.Now.AddHours(24), staffId: shop.StaffB);

        var error = await Json.ErrorAsync(await Reschedule(shop.CustomerId, inside, On(Day, 10)), HttpStatusCode.UnprocessableEntity, "CancellationWindowPassed");

        Assert.Equal(24, error.GetProperty("details").GetProperty("cancellationWindowHours").GetInt32());
        await AssertUnchangedAsync(inside, fixture.Now.AddHours(23), shop.StaffA);
        await OkAsync(Reschedule(shop.CustomerId, exactly, On(Day, 10)));
    }

    [Fact]
    public async Task R5_staff_move_their_own_bookings_to_their_own_slots_only()
    {
        var shop = await ShopAsync();
        var mine = await BookingAsync(shop, fixture.Now.AddHours(3)); // inside the window: no matter for staff
        var colleagues = await BookingAsync(shop, Far, staffId: shop.StaffB);

        await Json.ErrorAsync(await Reschedule(shop.UserA, mine, On(Day, 10), staffId: shop.StaffB), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await Reschedule(shop.UserA, colleagues, On(Day, 10)), HttpStatusCode.NotFound, "NotFound");
        await OkAsync(Reschedule(shop.UserA, mine, On(Day, 10)));

        Assert.Equal("Staff", (await EventAsync(mine, "BookingRescheduled")).GetProperty("rescheduledBy").GetProperty("role").GetString());
    }

    [Fact]
    public async Task R5_the_owner_moves_any_booking_to_any_staff_member()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, fixture.Now.AddDays(-1)); // even one that has ended

        var body = await OkAsync(Reschedule(shop.OwnerId, id, On(Day, 9), staffId: shop.StaffB));

        Assert.Equal(shop.StaffB, body.GetProperty("staffId").GetGuid());
        var history = (await fixture.Sql.RowAsync("SELECT * FROM booking_history WHERE booking_id = @id", new { id }))!;
        Assert.Equal("Owner", history["actor_role"]);
        Assert.Equal(shop.StaffB, Guid.Parse(Convert.ToString(history["staff_id"])!));
        Assert.Equal(shop.StaffA, Guid.Parse(Convert.ToString(history["previous_staff_id"])!));
        var data = await EventAsync(id, "BookingRescheduled");
        Assert.Equal(shop.StaffA, data.GetProperty("oldSlot").GetProperty("staffId").GetGuid());
        Assert.Equal(shop.StaffB, data.GetProperty("newSlot").GetProperty("staffId").GetGuid());
    }

    [Fact]
    public async Task R3_R8_the_new_slot_must_be_offered_and_outside_the_notice_period()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);

        await Json.ErrorAsync(await Reschedule(shop.CustomerId, id, On(Day, 9, 10)), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Reschedule(shop.CustomerId, id, On(Day, 13)), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Reschedule(shop.CustomerId, id, On(Day.AddDays(-4), 10)), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET min_notice_minutes = 5760 WHERE id = @id", new { id = shop.BusinessId });
        var error = await Json.ErrorAsync(await Reschedule(shop.CustomerId, id, On(Day, 10)), HttpStatusCode.UnprocessableEntity, "MinimumNotice");
        Assert.Equal(5760, error.GetProperty("details").GetProperty("minNoticeMinutes").GetInt32());
        await AssertUnchangedAsync(id, Far, shop.StaffA);
    }

    [Fact]
    public async Task R3_owners_and_the_booking_staff_member_override_the_notice_on_a_move_and_history_records_it()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET min_notice_minutes = 5760 WHERE id = @id", new { id = shop.BusinessId });
        var byOwner = await BookingAsync(shop, Far.AddDays(2));
        var byStaff = await BookingAsync(shop, Far.AddDays(2), staffId: shop.StaffB);

        await OkAsync(Reschedule(shop.OwnerId, byOwner, On(Day, 10), overrideNotice: true));
        await OkAsync(Reschedule(shop.UserB, byStaff, On(Day, 10), overrideNotice: true));

        foreach (var (id, role) in new[] { (byOwner, "Owner"), (byStaff, "Staff") })
        {
            var history = (await fixture.Sql.RowAsync("SELECT * FROM booking_history WHERE booking_id = @id", new { id }))!;
            Assert.True(Convert.ToBoolean(history["minimum_notice_overridden"]));
            Assert.Equal(role, history["actor_role"]);
        }
    }

    [Fact]
    public async Task R3_the_override_still_refuses_a_conflict_and_is_recorded_only_when_needed()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        await BookingAsync(shop, On(Day, 10), staffId: shop.StaffB, customerId: await fixture.Sql.InsertUserAsync());

        await Json.ErrorAsync(await Reschedule(shop.OwnerId, id, On(Day, 10, 30), staffId: shop.StaffB, overrideNotice: true),
            HttpStatusCode.Conflict, "SlotConflict");
        await OkAsync(Reschedule(shop.OwnerId, id, On(Day, 9), overrideNotice: true)); // outside the notice anyway

        var history = (await fixture.Sql.RowAsync("SELECT * FROM booking_history WHERE booking_id = @id", new { id }))!;
        Assert.False(Convert.ToBoolean(history["minimum_notice_overridden"]));
    }

    [Fact]
    public async Task R1_R5_a_move_may_overlap_the_booking_own_current_time_but_not_another_booking()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, On(Day, 10)); // blocks 09:45-11:15
        var other = await BookingAsync(shop, Far, customerId: await fixture.Sql.InsertUserAsync());

        await OkAsync(Reschedule(shop.CustomerId, id, On(Day, 10, 30))); // overlaps only itself (ADR 0003)

        var error = await Json.ErrorAsync(await Reschedule(shop.CustomerId, id, Far.AddMinutes(30)), HttpStatusCode.Conflict, "SlotConflict");
        var details = error.GetProperty("details");
        Assert.Equal(["endUtc", "staffId", "startUtc"], details.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Iso(Far.AddMinutes(30)), details.GetProperty("startUtc").GetString());
        Assert.DoesNotContain(other.ToString(), error.ToString());
        Assert.Equal(On(Day, 10, 30), (DateTime)(await RowAsync(id))["start_utc"]!); // kept its slot
    }

    [Fact]
    public async Task R5_R8_the_target_staff_member_must_be_active_assigned_and_in_the_business()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        var removed = (await StaffAsync(shop.BusinessId, shop.ServiceId)).Staff;
        await fixture.Sql.RemoveStaffAsync(removed);
        var unassigned = await fixture.Sql.InsertStaffAsync(shop.BusinessId);
        var outside = await fixture.Sql.InsertStaffAsync(await fixture.Sql.InsertBusinessAsync());

        await Json.ErrorAsync(await Reschedule(shop.OwnerId, id, On(Day, 10), staffId: removed), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Reschedule(shop.OwnerId, id, On(Day, 10), staffId: unassigned), HttpStatusCode.UnprocessableEntity, "StaffNotAssignedToService");
        await Json.ErrorAsync(await Reschedule(shop.OwnerId, id, On(Day, 10), staffId: outside), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Reschedule(shop.OwnerId, id, On(Day, 10), staffId: Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
        await AssertUnchangedAsync(id, Far, shop.StaffA);
    }

    [Fact]
    public async Task R5_a_cancelled_booking_cannot_be_rescheduled_and_others_get_404()
    {
        var shop = await ShopAsync();
        var cancelled = await BookingAsync(shop, Far, cancelled: true);
        var id = await BookingAsync(shop, Far, staffId: shop.StaffB);

        await Json.ErrorAsync(await Reschedule(shop.OwnerId, cancelled, On(Day, 10)), HttpStatusCode.UnprocessableEntity, "BookingCancelled");
        foreach (var caller in new[] { shop.UserA, await fixture.Sql.InsertUserAsync(), await fixture.Sql.InsertAdminUserAsync() })
        {
            await Json.ErrorAsync(await Reschedule(caller, id, On(Day, 10)), HttpStatusCode.NotFound, "NotFound");
        }
    }

    [Fact]
    public async Task R5_rescheduling_to_the_current_slot_changes_nothing()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        var near = await BookingAsync(shop, fixture.Now.AddHours(3), staffId: shop.StaffB);

        var body = await OkAsync(Reschedule(shop.CustomerId, id, Far, staffId: shop.StaffA));

        Assert.Equal(Iso(Far), body.GetProperty("startUtc").GetString());
        await AssertUnchangedAsync(id, Far, shop.StaffA);
        // The window is checked first.
        await Json.ErrorAsync(await Reschedule(shop.CustomerId, near, fixture.Now.AddHours(3)), HttpStatusCode.UnprocessableEntity, "CancellationWindowPassed");
    }

    [Fact]
    public async Task R4_R5_a_customer_who_is_also_the_owner_moves_inside_the_window_as_the_owner()
    {
        var shop = await ShopAsync();
        var near = await BookingAsync(shop, fixture.Now.AddHours(3), customerId: shop.OwnerId);
        var far = await BookingAsync(shop, Far, staffId: shop.StaffB, customerId: shop.OwnerId);

        await OkAsync(Reschedule(shop.OwnerId, near, On(Day, 9)));
        await OkAsync(Reschedule(shop.OwnerId, far, On(Day, 9)));

        Assert.Equal("Owner", (await EventAsync(near, "BookingRescheduled")).GetProperty("rescheduledBy").GetProperty("role").GetString());
        Assert.Equal("Customer", (await EventAsync(far, "BookingRescheduled")).GetProperty("rescheduledBy").GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("{}", "startUtc")]
    [InlineData("""{"startUtc":"2026-12-01T10:00:00+01:00"}""", "startUtc")]
    [InlineData("""{"startUtc":"2026-12-01T10:00:00Z","staffId":"abc"}""", "staffId")]
    [InlineData("""{"startUtc":"2026-12-01T10:00:00Z","colour":"red"}""", "")]
    public async Task R5_invalid_reschedule_bodies_are_ValidationFailed(string json, string field)
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);

        var error = await Json.ErrorAsync(await fixture.Api.ClientFor(shop.CustomerId).PostAsync($"/v1/bookings/{id}/reschedule", Json.Body(json)),
            HttpStatusCode.BadRequest, "ValidationFailed");

        if (field != "") Assert.Contains(Json.FieldNames(error), f => f.StartsWith(field, StringComparison.Ordinal));
        await AssertUnchangedAsync(id, Far, shop.StaffA);
    }

    // ------------------------------------------------------------ Reschedule locks (ADR 0002, T15 code review)

    [Fact]
    public async Task ADR0002_reschedule_locks_the_customer_row_exclusively_before_any_staff_row()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        Task<HttpResponseMessage> move;

        // Even a share lock on the customer's row (a cancel holds one) makes the reschedule wait, holding no staff lock.
        await using (var held = await fixture.Sql.LockUserRowAsync(shop.CustomerId, shared: true))
        {
            move = Reschedule(shop.OwnerId, id, On(Day, 10), staffId: shop.StaffB);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id IN (@a, @b) FOR UPDATE NOWAIT", new { a = shop.StaffA, b = shop.StaffB });
        }
        await OkAsync(move);
    }

    [Fact]
    public async Task ADR0002_a_move_between_staff_waits_for_both_staff_rows()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        Task<HttpResponseMessage> move;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffB)) // the target staff member
        {
            move = Reschedule(shop.OwnerId, id, On(Day, 10), staffId: shop.StaffB);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            // Staff B gets a booking at that time while the move waits; the move must see it.
            await held.ExecuteAsync(Sql.InsertBookingSql, new
            {
                id = Guid.NewGuid(), staff = shop.StaffB, service = shop.ServiceId, customer = shop.UserA, start = On(Day, 10), end = On(Day, 11),
                blockedStart = On(Day, 9, 45), blockedEnd = On(Day, 11, 15), status = "Confirmed", now = fixture.Now,
                cancelledAt = (DateTime?)null, cancelledBy = (Guid?)null, reason = (string?)null,
            });
            await held.CommitAsync();
        }

        await Json.ErrorAsync(await move, HttpStatusCode.Conflict, "SlotConflict");
        await AssertUnchangedAsync(id, Far, shop.StaffA);
    }

    [Fact]
    public async Task R4_a_booking_made_while_a_reschedule_moves_into_the_window_sees_the_move()
    {
        // T15 code review: the rebook check relies on reschedule holding the customer's row.
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET cancellation_window_hours = 96 WHERE id = @id", new { id = shop.BusinessId });
        var id = await BookingAsync(shop, Far.AddDays(2)); // outside the 96-hour window
        Task<HttpResponseMessage> move, book;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            move = Reschedule(shop.CustomerId, id, On(Day, 10)); // into the window; waits for staff A holding the customer's row
            await fixture.Sql.WaitForLockWaitAsync("staff");
            book = fixture.Api.ClientFor(shop.CustomerId).PostJsonAsync("/v1/bookings",
                new { serviceId = shop.ServiceId, staffId = shop.StaffB, startUtc = Iso(On(Day.AddDays(1), 10)) });
            await fixture.Sql.WaitForLockWaitAsync("users"); // the booking waits for the customer's row
        }

        await OkAsync(move);
        var error = await Json.ErrorAsync(await book, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal(id, Assert.Single(error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray()).GetProperty("bookingId").GetGuid());
    }

    [Fact]
    public async Task ADR0002_a_cancel_whose_booking_moved_staff_before_its_locks_locks_the_new_staff_member()
    {
        // Code review 2026-11-20: the cancel reads the booking's staff member after taking the user rows (no move
        // can commit after that), so a move committed before then makes it lock the new staff member.
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        Task<HttpResponseMessage> cancel;

        var heldCustomer = await fixture.Sql.LockUserRowAsync(shop.CustomerId);
        try
        {
            cancel = Cancel(shop.OwnerId, id); // has read staff A; now waits for the customer's row
            await fixture.Sql.WaitForLockWaitAsync("users");
            await fixture.Sql.ExecuteAsync("UPDATE bookings SET staff_id = @b WHERE id = @id", new { b = shop.StaffB, id }); // the move commits
            await using var heldStaffB = await fixture.Sql.LockRowAsync("staff", shop.StaffB);
            await heldCustomer.DisposeAsync();
            // It takes staff B's lock itself. (Locking staff A instead, it would still wait on staff B, but only in
            // its UPDATE of the booking, through the staff foreign key's index, after checking unlocked.)
            var waiting = await fixture.Sql.WaitForLockWaitAsync("staff");
            Assert.Contains("FROM staff", waiting);
            Assert.Contains("FOR UPDATE", waiting);
            Assert.False(cancel.IsCompleted);
        }
        finally
        {
            await heldCustomer.DisposeAsync();
        }

        Assert.Equal(shop.StaffB, (await OkAsync(cancel)).GetProperty("staffId").GetGuid());
    }

    [Fact]
    public async Task R4_two_simultaneous_cancels_give_one_success_and_one_BookingCancelled()
    {
        // Code review 2026-11-20: the second cancel must read the booking as the first left it, not as it was
        // before it waited for the staff lock.
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        Task<HttpResponseMessage> byStaff, byOwner;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            byStaff = Cancel(shop.UserA, id);
            byOwner = Cancel(shop.OwnerId, id);
            await fixture.Sql.WaitForLockWaitsAsync("staff", 2);
        }

        var responses = await Task.WhenAll(byStaff, byOwner);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity], responses.Select(r => r.StatusCode).Order());
        await Json.ErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity), HttpStatusCode.UnprocessableEntity, "BookingCancelled");
        Assert.Equal(2, Convert.ToInt32((await RowAsync(id))["version"]));
        Assert.Equal(1, await EventCountAsync(id));
    }

    [Fact]
    public async Task R4_R5_a_cancel_and_a_reschedule_at_once_never_fail_with_500()
    {
        var shop = await ShopAsync();
        var id = await BookingAsync(shop, Far);
        Task<HttpResponseMessage> cancel, move;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            cancel = Cancel(shop.UserA, id);
            move = Reschedule(shop.OwnerId, id, On(Day, 10));
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await fixture.Sql.WaitForLockWaitAsync("users"); // the move needs the customer's row, held shared by the cancel
        }

        var statuses = (await Task.WhenAll(cancel, move)).Select(r => (int)r.StatusCode).ToList();
        Assert.All(statuses, s => Assert.True(s < 500, $"got {s}"));
        Assert.Equal("Cancelled", (await RowAsync(id))["status"]);
    }
}
