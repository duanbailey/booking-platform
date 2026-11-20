using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T15: R4's rebook paragraph on POST /v1/bookings, through the confirm pattern. Stories "Customer cancels
/// or reschedules" (last scenario) and "Staff or owner books on behalf of a customer" (R4 scenario).
/// Decisions 2026-11-20 (plan/tasks.md, T15): inside the window = Confirmed, same business, now &lt; start &lt;
/// now + N hours; exact-set match; ConfirmationRequired checked last; the customer's user row is locked FOR
/// UPDATE; staff see colleagues' bookings in the list; one RebookedInsideWindow per original.
/// The business is in UTC with the default 24-hour window. Staff A and B are available 09:00-12:00 every
/// day; the service lasts 60 minutes with 15-minute buffers. New bookings are made three days ahead, and
/// "originals" are inserted directly a few hours from now (inside the window).
/// </summary>
public class RebookInsideWindowTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private sealed record Shop(
        Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB,
        Guid CustomerId, string CustomerEmail);

    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private DateTime NewStart => Day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @business", new { business });
        var service = await fixture.Sql.InsertServiceAsync(business);

        async Task<(Guid Staff, Guid User)> StaffAsync()
        {
            var user = await fixture.Sql.InsertUserAsync(passwordHash: "hash", name: "Sam Staff");
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
        var email = ApiFixture.NewEmail();
        var customer = await fixture.Sql.InsertUserAsync(email, "hash", "Cara Customer");
        return new Shop(owner, business, service, a.Staff, a.User, b.Staff, b.User, customer, email);
    }

    /// <summary>Inserts a booking for the customer starting <paramref name="fromNow"/> after now, lasting an hour.</summary>
    private async Task<Guid> OriginalAsync(Shop shop, TimeSpan fromNow, Guid? staffId = null, Guid? customerId = null,
        Guid? serviceId = null, bool cancelled = false)
    {
        var start = fixture.Now + fromNow;
        var parameters = Sql.BookingParameters(new(shop.BusinessId, staffId ?? shop.StaffA, serviceId ?? shop.ServiceId, customerId ?? shop.CustomerId));
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

    private Dictionary<string, object?> Body(Shop shop, Guid? staffId = null, string? customerEmail = null, Guid[]? confirm = null)
    {
        var body = new Dictionary<string, object?> { ["serviceId"] = shop.ServiceId, ["staffId"] = staffId ?? shop.StaffA, ["startUtc"] = Iso(NewStart) };
        if (customerEmail is not null) body["customerEmail"] = customerEmail;
        if (confirm is not null) body["confirm"] = new { affectedBookingIds = confirm };
        return body;
    }

    private Task<HttpResponseMessage> Book(Guid callerId, object body) => fixture.Api.ClientFor(callerId).PostJsonAsync("/v1/bookings", body);

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    /// <summary>Bookings made through the API in these tests start at NewStart; originals never do.</summary>
    private Task<long> NewBookingsAsync(Shop shop) =>
        CountAsync("SELECT COUNT(*) FROM bookings WHERE service_id = @service AND start_utc = @start", new { service = shop.ServiceId, start = NewStart });

    private static async Task<JsonElement> ConfirmationRequiredAsync(HttpResponseMessage response)
    {
        var error = await Json.ErrorAsync(response, HttpStatusCode.Conflict, "ConfirmationRequired");
        Assert.Equal("R4", error.GetProperty("rule").GetString());
        return error;
    }

    private static List<Guid> AffectedIds(JsonElement error) =>
        error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray().Select(b => b.GetProperty("bookingId").GetGuid()).ToList();

    /// <summary>The RebookedInsideWindow payloads for an original booking, each validated against its schema.</summary>
    private async Task<List<JsonElement>> RebookedEventsAsync(Guid originalId)
    {
        var events = new List<JsonElement>();
        var count = await CountAsync(
            "SELECT COUNT(*) FROM outbox WHERE event_type = 'RebookedInsideWindow' AND message_group_id = @id", new { id = originalId.ToString() });
        for (var i = 0; i < count; i++)
        {
            var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
                "SELECT payload FROM outbox WHERE event_type = 'RebookedInsideWindow' AND message_group_id = @id ORDER BY created_at, id LIMIT 1 OFFSET " + i,
                new { id = originalId.ToString() }))!;
            EventSchemas.AssertValid(payload);
            events.Add(JsonDocument.Parse(payload).RootElement.GetProperty("data"));
        }
        return events;
    }

    private Task<long> RebookedEventCountAsync(Shop shop) => CountAsync(
        "SELECT COUNT(*) FROM outbox WHERE event_type = 'RebookedInsideWindow' AND payload->>'$.data.businessId' = @business",
        new { business = shop.BusinessId.ToString() });

    // ------------------------------------------------------------ Story: the customer books again inside the window

    [Fact]
    public async Task R4_a_customer_holding_a_booking_inside_the_window_is_warned_and_nothing_changes()
    {
        var shop = await ShopAsync();
        var original = await OriginalAsync(shop, TimeSpan.FromHours(5));

        var error = await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop)));

        var listed = Assert.Single(error.GetProperty("details").GetProperty("affectedBookings").EnumerateArray());
        Assert.Equal(["bookingId", "endUtc", "staffId", "startUtc"], listed.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(original, listed.GetProperty("bookingId").GetGuid());
        Assert.Equal(shop.StaffA, listed.GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(fixture.Now.AddHours(5)), listed.GetProperty("startUtc").GetString());
        Assert.Equal(Iso(fixture.Now.AddHours(6)), listed.GetProperty("endUtc").GetString());
        // A customer booking for themselves is told the original still stands and to contact the business.
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("still stands", message);
        Assert.Contains("contact the business", message);
        Assert.Equal(0, await NewBookingsAsync(shop));
        Assert.Equal(0, await RebookedEventCountAsync(shop));
    }

    [Fact]
    public async Task R4_on_confirmation_the_new_booking_is_made_the_original_kept_and_the_business_notified()
    {
        var shop = await ShopAsync();
        var original = await OriginalAsync(shop, TimeSpan.FromHours(5));

        var body = await Json.ReadAsync(await Book(shop.CustomerId, Body(shop, confirm: [original])), HttpStatusCode.Created);

        var newId = body.GetProperty("id").GetGuid();
        Assert.Equal("Confirmed", await fixture.Sql.ScalarAsync("SELECT status FROM bookings WHERE id = @original", new { original }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM outbox WHERE event_type = 'BookingCreated' AND message_group_id = @id", new { id = newId.ToString() }));
        var data = Assert.Single(await RebookedEventsAsync(original));
        Assert.Equal(shop.BusinessId, data.GetProperty("businessId").GetGuid());
        Assert.Equal("UTC", data.GetProperty("businessTimeZone").GetString());
        Assert.Equal(shop.CustomerId, data.GetProperty("customerId").GetGuid());
        Assert.Equal(original, data.GetProperty("originalBookingId").GetGuid());
        Assert.Equal(shop.StaffA, data.GetProperty("originalSlot").GetProperty("staffId").GetGuid());
        Assert.Equal(fixture.Now.AddHours(5), data.GetProperty("originalSlot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(newId, data.GetProperty("newBookingId").GetGuid());
        Assert.Equal(shop.StaffA, data.GetProperty("newSlot").GetProperty("staffId").GetGuid());
        Assert.Equal(NewStart, data.GetProperty("newSlot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(NewStart.AddHours(1), data.GetProperty("newSlot").GetProperty("endUtc").GetDateTime().ToUniversalTime());
    }

    // ------------------------------------------------------------ Story: on behalf of a customer

    [Fact]
    public async Task R4_the_owner_booking_on_behalf_is_warned_without_being_told_to_contact_the_business()
    {
        var shop = await ShopAsync();
        var original = await OriginalAsync(shop, TimeSpan.FromHours(5));

        var error = await ConfirmationRequiredAsync(await Book(shop.OwnerId, Body(shop, customerEmail: shop.CustomerEmail)));

        Assert.Equal([original], AffectedIds(error));
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("still stands", message);
        Assert.DoesNotContain("contact the business", message);

        await Json.ReadAsync(await Book(shop.OwnerId, Body(shop, customerEmail: shop.CustomerEmail, confirm: [original])), HttpStatusCode.Created);
        Assert.Single(await RebookedEventsAsync(original));
    }

    [Fact]
    public async Task R4_staff_booking_on_behalf_see_the_customer_bookings_with_colleagues_too()
    {
        var shop = await ShopAsync();
        var withColleague = await OriginalAsync(shop, TimeSpan.FromHours(5), staffId: shop.StaffB); // decided 2026-11-20

        var error = await ConfirmationRequiredAsync(await Book(shop.UserA, Body(shop, customerEmail: shop.CustomerEmail)));

        Assert.Equal([withColleague], AffectedIds(error));
        await Json.ReadAsync(await Book(shop.UserA, Body(shop, customerEmail: shop.CustomerEmail, confirm: [withColleague])), HttpStatusCode.Created);
        Assert.Equal(shop.StaffB, Assert.Single(await RebookedEventsAsync(withColleague)).GetProperty("originalSlot").GetProperty("staffId").GetGuid());
    }

    // ------------------------------------------------------------ Which bookings are inside the window

    [Fact]
    public async Task R4_a_booking_exactly_N_hours_away_is_outside_the_window_and_one_just_inside_counts()
    {
        var shop = await ShopAsync();
        await OriginalAsync(shop, TimeSpan.FromHours(24)); // cancelling it now is still allowed (inclusive)

        await Json.ReadAsync(await Book(shop.CustomerId, Body(shop)), HttpStatusCode.Created);

        var other = await ShopAsync();
        var inside = await OriginalAsync(other, TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal([inside], AffectedIds(await ConfirmationRequiredAsync(await Book(other.CustomerId, Body(other)))));
    }

    [Fact]
    public async Task R4_started_past_and_cancelled_bookings_do_not_count()
    {
        var shop = await ShopAsync();
        await OriginalAsync(shop, TimeSpan.Zero);                   // starts now: started
        await OriginalAsync(shop, TimeSpan.FromMinutes(-30));       // in progress
        await OriginalAsync(shop, TimeSpan.FromDays(-2));           // past
        await OriginalAsync(shop, TimeSpan.FromHours(5), cancelled: true);

        await Json.ReadAsync(await Book(shop.CustomerId, Body(shop)), HttpStatusCode.Created);
        Assert.Equal(0, await RebookedEventCountAsync(shop));
    }

    [Fact]
    public async Task R4_other_customers_bookings_and_bookings_with_another_business_do_not_count()
    {
        var shop = await ShopAsync();
        await OriginalAsync(shop, TimeSpan.FromHours(5), customerId: await fixture.Sql.InsertUserAsync());
        var elsewhere = await ShopAsync();
        await OriginalAsync(elsewhere, TimeSpan.FromHours(5), customerId: shop.CustomerId);

        await Json.ReadAsync(await Book(shop.CustomerId, Body(shop)), HttpStatusCode.Created);
    }

    [Fact]
    public async Task R4_a_booking_for_another_service_or_staff_member_of_the_business_counts()
    {
        var shop = await ShopAsync();
        var colour = await fixture.Sql.InsertServiceAsync(shop.BusinessId, "Colour");
        var otherService = await OriginalAsync(shop, TimeSpan.FromHours(3), serviceId: colour);
        var otherStaff = await OriginalAsync(shop, TimeSpan.FromHours(8), staffId: shop.StaffB);

        var error = await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop)));

        Assert.Equal([otherService, otherStaff], AffectedIds(error)); // ordered by start
    }

    [Fact]
    public async Task R4_a_zero_hour_window_never_warns()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET cancellation_window_hours = 0 WHERE id = @id", new { id = shop.BusinessId });
        await OriginalAsync(shop, TimeSpan.FromMinutes(5));

        await Json.ReadAsync(await Book(shop.CustomerId, Body(shop)), HttpStatusCode.Created);
    }

    [Fact]
    public async Task R4_the_window_is_the_business_setting()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET cancellation_window_hours = 2 WHERE id = @id", new { id = shop.BusinessId });
        await OriginalAsync(shop, TimeSpan.FromHours(3));

        await Json.ReadAsync(await Book(shop.CustomerId, Body(shop)), HttpStatusCode.Created);
    }

    // ------------------------------------------------------------ Several originals, one event each

    [Fact]
    public async Task R4_several_originals_are_listed_by_start_and_each_gets_an_event()
    {
        var shop = await ShopAsync();
        var later = await OriginalAsync(shop, TimeSpan.FromHours(10));
        var sooner = await OriginalAsync(shop, TimeSpan.FromHours(2), staffId: shop.StaffB);

        Assert.Equal([sooner, later], AffectedIds(await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop)))));

        var created = await Json.ReadAsync(await Book(shop.CustomerId, Body(shop, confirm: [later, sooner])), HttpStatusCode.Created);
        var newId = created.GetProperty("id").GetGuid();
        Assert.Equal(newId, Assert.Single(await RebookedEventsAsync(sooner)).GetProperty("newBookingId").GetGuid());
        Assert.Equal(newId, Assert.Single(await RebookedEventsAsync(later)).GetProperty("newBookingId").GetGuid());
        Assert.Equal(2, await RebookedEventCountAsync(shop));
    }

    // ------------------------------------------------------------ The confirm pattern: exact set, recomputed

    [Fact]
    public async Task R4_a_confirmation_naming_only_some_of_the_originals_is_asked_again_with_the_full_list()
    {
        var shop = await ShopAsync();
        var a = await OriginalAsync(shop, TimeSpan.FromHours(2));
        var b = await OriginalAsync(shop, TimeSpan.FromHours(10));

        Assert.Equal([a, b], AffectedIds(await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop, confirm: [a])))));
        Assert.Equal([a, b], AffectedIds(await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop, confirm: [a, b, Guid.NewGuid()])))));
        Assert.Equal(0, await NewBookingsAsync(shop));
    }

    [Fact]
    public async Task R4_a_booking_that_enters_the_window_after_the_warning_is_asked_about_again()
    {
        var shop = await ShopAsync();
        var first = await OriginalAsync(shop, TimeSpan.FromHours(2));
        Assert.Equal([first], AffectedIds(await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop)))));
        var second = await OriginalAsync(shop, TimeSpan.FromHours(6));

        var again = await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop, confirm: [first])));

        Assert.Equal([first, second], AffectedIds(again));
        Assert.Equal(0, await NewBookingsAsync(shop));
    }

    [Fact]
    public async Task R4_a_confirmation_that_is_no_longer_needed_goes_ahead()
    {
        var shop = await ShopAsync();
        var original = await OriginalAsync(shop, TimeSpan.FromHours(2));
        await ConfirmationRequiredAsync(await Book(shop.CustomerId, Body(shop)));
        await fixture.Sql.ExecuteAsync(
            "UPDATE bookings SET status = 'Cancelled', cancelled_at = @now, cancelled_by = @by, cancellation_reason = 'BusinessCancelled' WHERE id = @original",
            new { now = fixture.Now, by = shop.OwnerId, original });

        await Json.ReadAsync(await Book(shop.CustomerId, Body(shop, confirm: [original])), HttpStatusCode.Created);
        Assert.Equal(0, await RebookedEventCountAsync(shop));
    }

    // ------------------------------------------------------------ Check order: ConfirmationRequired is last

    [Fact]
    public async Task R4_other_refusals_come_before_ConfirmationRequired()
    {
        var shop = await ShopAsync();
        await OriginalAsync(shop, TimeSpan.FromHours(5));
        var taken = Sql.BookingParameters(new(shop.BusinessId, shop.StaffB, shop.ServiceId, await fixture.Sql.InsertUserAsync()));
        taken["start"] = NewStart;
        taken["end"] = NewStart.AddHours(1);
        taken["blockedStart"] = NewStart.AddMinutes(-15);
        taken["blockedEnd"] = NewStart.AddMinutes(75);
        await fixture.Sql.ExecuteAsync(Sql.InsertBookingSql, taken);

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, staffId: shop.StaffB)), HttpStatusCode.Conflict, "SlotConflict");
        var offStep = Body(shop);
        offStep["startUtc"] = Iso(NewStart.AddMinutes(10));
        await Json.ErrorAsync(await Book(shop.CustomerId, offStep), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET min_notice_minutes = 5760 WHERE id = @id", new { id = shop.BusinessId });
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop)), HttpStatusCode.UnprocessableEntity, "MinimumNotice");
    }

    // ------------------------------------------------------------ Locks: one customer's bookings run one at a time

    [Fact]
    public async Task R4_the_customer_row_is_locked_exclusively_so_a_booking_committed_meanwhile_is_seen()
    {
        var shop = await ShopAsync();
        Task<HttpResponseMessage> book;
        Guid sneaked;

        // A share lock on the customer's row (as another booking's FOR SHARE would be) makes this booking wait.
        await using (var held = await fixture.Sql.LockUserRowAsync(shop.CustomerId, shared: true))
        {
            book = Book(shop.CustomerId, Body(shop));
            await fixture.Sql.WaitForLockWaitAsync("users");
            sneaked = Guid.NewGuid();
            var start = fixture.Now.AddHours(4);
            await held.ExecuteAsync(Sql.InsertBookingSql, new
            {
                id = sneaked, staff = shop.StaffB, service = shop.ServiceId, customer = shop.CustomerId, start, end = start.AddHours(1),
                blockedStart = start.AddMinutes(-15), blockedEnd = start.AddMinutes(75), status = "Confirmed", now = fixture.Now,
                cancelledAt = (DateTime?)null, cancelledBy = (Guid?)null, reason = (string?)null,
            });
            await held.CommitAsync();
        }

        Assert.Equal([sneaked], AffectedIds(await ConfirmationRequiredAsync(await book)));
    }

    [Fact]
    public async Task R4_the_caller_row_stays_a_share_lock_when_booking_on_behalf()
    {
        var shop = await ShopAsync();

        await using var held = await fixture.Sql.LockUserRowAsync(shop.OwnerId, shared: true);
        var response = await Book(shop.OwnerId, Body(shop, customerEmail: shop.CustomerEmail)).WaitAsync(TimeSpan.FromSeconds(10));

        await Json.ReadAsync(response, HttpStatusCode.Created);
    }

    /// <summary>
    /// Code review 2026-11-20: a staff member can cancel the customer's booking under their own staff lock
    /// alone, so the R4 check re-reads each original with a locking read. (Moving one is different: changing
    /// start_utc also locks the customer's row through the customer foreign key's index, and reschedule must
    /// take that row first anyway (T17), so a move waits for this booking and is then seen fresh.)
    /// </summary>
    [Fact]
    public async Task R4_an_original_cancelled_by_its_staff_meanwhile_is_not_listed_or_notified()
    {
        var shop = await ShopAsync();
        var original = await OriginalAsync(shop, TimeSpan.FromHours(5), staffId: shop.StaffB);
        Task<HttpResponseMessage> book;

        // Staff B's cancellation holds the original's row (under staff B's lock only, not the customer's) while the booking runs.
        await using (var held = await fixture.Sql.LockRowAsync("bookings", original))
        {
            book = Book(shop.CustomerId, Body(shop));
            await fixture.Sql.WaitForLockWaitAsync("bookings"); // the R4 check reads the originals with a locking read
            await held.ExecuteAsync(
                "UPDATE bookings SET status = 'Cancelled', cancelled_at = @now, cancelled_by = @by, cancellation_reason = 'BusinessCancelled' WHERE id = @original",
                new { now = fixture.Now, by = shop.UserB, original });
            await held.CommitAsync();
        }

        await Json.ReadAsync(await book, HttpStatusCode.Created);
        Assert.Empty(await RebookedEventsAsync(original));
    }
}
