using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;
using NodaTime;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T14: POST /v1/bookings without the R4 rebook check (T15). Stories "Customer books a slot", "Staff or
/// owner books on behalf of a customer" (except R4) and "Owner or staff overrides the minimum notice
/// period" (booking scenarios); R1, R3, R7, R8; ADR 0002 (staff lock) and ADR 0004 (buffer snapshot).
/// Decisions 2026-11-20 are in plan/tasks.md (T14): check order, 409 details = the requested slot,
/// history actor role, override recorded only when used, user rows locked FOR SHARE before the staff row.
/// The business is in UTC, so local and UTC times coincide. Staff A and B are available 09:00-12:00 every
/// day; the service lasts 60 minutes with 15-minute buffers, so each day offers 09:00 to 11:00.
/// </summary>
public class BookingsTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private sealed record Shop(
        Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB,
        Guid CustomerId, string CustomerEmail);

    /// <summary>Three days ahead: outside the default 2-hour notice, inside the 21-day window.</summary>
    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static DateTime On(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync(name: "Olive Owner");
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @business", new { business });
        var service = await fixture.Sql.InsertServiceAsync(business); // 60 minutes, 15-minute buffers

        async Task<(Guid Staff, Guid User)> StaffAsync()
        {
            var user = await fixture.Sql.InsertUserAsync(passwordHash: "hash", name: "Sam Staff");
            var staff = await fixture.Sql.InsertStaffAsync(business, user);
            await fixture.Sql.AssignStaffToServiceAsync(staff, service);
            await AvailableEveryDayAsync(staff);
            return (staff, user);
        }

        var a = await StaffAsync();
        var b = await StaffAsync();
        var email = ApiFixture.NewEmail();
        var customer = await fixture.Sql.InsertUserAsync(email, "hash", "Cara Customer");
        return new Shop(owner, business, service, a.Staff, a.User, b.Staff, b.User, customer, email);
    }

    private async Task AvailableEveryDayAsync(Guid staffId, string start = "09:00", string end = "12:00")
    {
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await fixture.Sql.ExecuteAsync(
                "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staffId, @day, @start, @end)",
                new { id = Guid.NewGuid(), staffId, day, start, end });
        }
    }

    private Task SetBusinessAsync(Shop shop, string assignment) =>
        fixture.Sql.ExecuteAsync($"UPDATE businesses SET {assignment} WHERE id = @id", new { id = shop.BusinessId });

    private static Dictionary<string, object?> Body(Shop shop, Guid staffId, DateTime start, string? customerEmail = null, bool? overrideNotice = null)
    {
        var body = new Dictionary<string, object?> { ["serviceId"] = shop.ServiceId, ["staffId"] = staffId, ["startUtc"] = Iso(start) };
        if (customerEmail is not null) body["customerEmail"] = customerEmail;
        if (overrideNotice is not null) body["overrideMinimumNotice"] = overrideNotice;
        return body;
    }

    private Task<HttpResponseMessage> Book(Guid callerId, object body) => fixture.Api.ClientFor(callerId).PostJsonAsync("/v1/bookings", body);

    private Task<HttpResponseMessage> BookRaw(Guid callerId, string json) =>
        fixture.Api.ClientFor(callerId).PostAsync("/v1/bookings", Json.Body(json));

    private static async Task<JsonElement> CreatedAsync(Task<HttpResponseMessage> request) => await Json.ReadAsync(await request, HttpStatusCode.Created);

    private async Task<long> CountAsync(string sql, object parameters) => Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<long> BookingCountAsync(Shop shop) =>
        CountAsync("SELECT COUNT(*) FROM bookings WHERE service_id = @service", new { service = shop.ServiceId });

    /// <summary>A refused request writes nothing: no booking, no history, no event.</summary>
    private async Task AssertNothingWrittenAsync(Shop shop)
    {
        Assert.Equal(0, await BookingCountAsync(shop));
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM booking_history h JOIN bookings b ON b.id = h.booking_id WHERE b.service_id = @service", new { service = shop.ServiceId }));
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM outbox WHERE event_type = 'BookingCreated' AND payload->>'$.data.serviceId' = @service",
            new { service = shop.ServiceId.ToString() }));
    }

    private async Task<Dictionary<string, object?>> HistoryAsync(Guid bookingId) =>
        (await fixture.Sql.RowAsync("SELECT * FROM booking_history WHERE booking_id = @bookingId", new { bookingId }))!;

    /// <summary>The booking's BookingCreated envelope, validated against its schema (T5 note).</summary>
    private async Task<JsonElement> BookingCreatedAsync(Guid bookingId)
    {
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM outbox WHERE message_group_id = @id", new { id = bookingId.ToString() }));
        var row = (await fixture.Sql.RowAsync(
            "SELECT event_type, schema_version, payload FROM outbox WHERE message_group_id = @id", new { id = bookingId.ToString() }))!;
        Assert.Equal("BookingCreated", row["event_type"]);
        var payload = Convert.ToString(row["payload"])!;
        EventSchemas.AssertValid(payload);
        return JsonDocument.Parse(payload).RootElement;
    }

    private static Guid Id(JsonElement booking) => booking.GetProperty("id").GetGuid();

    private static Guid AsGuid(object? value) => Guid.Parse(Convert.ToString(value)!);

    // ------------------------------------------------------------ Story: customer books a slot

    [Fact]
    public async Task R1_R3_customer_books_an_offered_slot_for_themselves()
    {
        var shop = await ShopAsync();
        var start = On(Day, 10);

        var response = await Book(shop.CustomerId, Body(shop, shop.StaffA, start));
        var body = await Json.ReadAsync(response, HttpStatusCode.Created);

        var id = Id(body);
        Assert.Equal($"/v1/bookings/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(
            ["businessId", "businessTimeZone", "cancellationReason", "cancelledAt", "customer", "endUtc", "id", "serviceId", "staffId", "startUtc", "status"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(shop.BusinessId, body.GetProperty("businessId").GetGuid());
        Assert.Equal("UTC", body.GetProperty("businessTimeZone").GetString());
        Assert.Equal(shop.ServiceId, body.GetProperty("serviceId").GetGuid());
        Assert.Equal(shop.StaffA, body.GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(start), body.GetProperty("startUtc").GetString());
        Assert.Equal(Iso(On(Day, 11)), body.GetProperty("endUtc").GetString());
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cancelledAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cancellationReason").ValueKind);
        var customer = body.GetProperty("customer");
        Assert.Equal(shop.CustomerId, customer.GetProperty("userId").GetGuid());
        Assert.Equal("Cara Customer", customer.GetProperty("name").GetString());
        Assert.Equal(shop.CustomerEmail, customer.GetProperty("email").GetString());

        // Stored Confirmed at version 1 (T14 note), with the buffers snapshotted (ADR 0004).
        var row = (await fixture.Sql.RowAsync("SELECT * FROM bookings WHERE id = @id", new { id }))!;
        Assert.Equal(shop.CustomerId, AsGuid(row["customer_id"]));
        Assert.Equal("Confirmed", row["status"]);
        Assert.Equal(1, Convert.ToInt32(row["version"]));
        Assert.Equal(On(Day, 9, 45), (DateTime)row["blocked_start_utc"]!);
        Assert.Equal(On(Day, 11, 15), (DateTime)row["blocked_end_utc"]!);
        Assert.Equal(fixture.Now, (DateTime)row["created_at"]!);

        // History: a creation row in the same transaction, by the customer acting as Customer.
        var history = await HistoryAsync(id);
        Assert.Equal("Created", history["type"]);
        Assert.Equal(shop.CustomerId, AsGuid(history["actor_user_id"]));
        Assert.Equal("Customer", history["actor_role"]);
        Assert.Equal(fixture.Now, (DateTime)history["at"]!);
        Assert.Equal(shop.StaffA, AsGuid(history["staff_id"]));
        Assert.Equal(start, (DateTime)history["start_utc"]!);
        Assert.Equal(On(Day, 11), (DateTime)history["end_utc"]!);
        Assert.Null(history["previous_staff_id"]);
        Assert.Null(history["previous_start_utc"]);
        Assert.Null(history["previous_end_utc"]);
        Assert.False(Convert.ToBoolean(history["minimum_notice_overridden"]));

        // BookingCreated, so the worker sends the confirmation email (T24).
        var envelope = await BookingCreatedAsync(id);
        var data = envelope.GetProperty("data");
        Assert.Equal(id, data.GetProperty("bookingId").GetGuid());
        Assert.Equal(1, data.GetProperty("bookingVersion").GetInt32());
        Assert.Equal(shop.BusinessId, data.GetProperty("businessId").GetGuid());
        Assert.Equal("UTC", data.GetProperty("businessTimeZone").GetString());
        Assert.Equal(shop.ServiceId, data.GetProperty("serviceId").GetGuid());
        Assert.Equal(shop.CustomerId, data.GetProperty("customerId").GetGuid());
        Assert.Equal(shop.StaffA, data.GetProperty("slot").GetProperty("staffId").GetGuid());
        // Event times carry milliseconds (T5), so compare instants.
        Assert.Equal(start, data.GetProperty("slot").GetProperty("startUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(On(Day, 11), data.GetProperty("slot").GetProperty("endUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(shop.CustomerId, data.GetProperty("createdBy").GetProperty("userId").GetGuid());
        Assert.Equal("Customer", data.GetProperty("createdBy").GetProperty("role").GetString());
    }

    [Fact]
    public async Task R1_R8_a_booked_slot_is_no_longer_offered()
    {
        var shop = await ShopAsync();

        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10))));

        var slots = await Json.ReadAsync(await fixture.Api.ClientFor(shop.CustomerId)
            .GetAsync($"/v1/services/{shop.ServiceId}/slots?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}&staffId={shop.StaffA}"), HttpStatusCode.OK);
        // 10:00 blocks 09:45-11:15, which every other start's blocked interval overlaps (09:00 to 10:15, 11:00 from 10:45).
        Assert.Empty(slots.GetProperty("slots").EnumerateArray());
    }

    [Fact]
    public async Task ADR0004_buffers_come_from_the_service_when_booked_and_later_changes_leave_the_booking_alone()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE services SET buffer_before_minutes = 30, buffer_after_minutes = 0 WHERE id = @id", new { id = shop.ServiceId });

        var id = Id(await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10)))));
        await fixture.Sql.ExecuteAsync("UPDATE services SET buffer_before_minutes = 5, buffer_after_minutes = 45 WHERE id = @id", new { id = shop.ServiceId });

        var row = (await fixture.Sql.RowAsync("SELECT blocked_start_utc, blocked_end_utc FROM bookings WHERE id = @id", new { id }))!;
        Assert.Equal(On(Day, 9, 30), (DateTime)row["blocked_start_utc"]!);
        Assert.Equal(On(Day, 11), (DateTime)row["blocked_end_utc"]!);
    }

    [Fact]
    public async Task Owner_and_staff_may_book_for_themselves_as_customers()
    {
        var shop = await ShopAsync();

        var byOwner = Id(await CreatedAsync(Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 9)))));
        var byStaff = Id(await CreatedAsync(Book(shop.UserA, Body(shop, shop.StaffA, On(Day, 10, 30)))));

        Assert.Equal("Customer", (await HistoryAsync(byOwner))["actor_role"]);
        Assert.Equal("Customer", (await HistoryAsync(byStaff))["actor_role"]);
        Assert.Equal(shop.UserA, AsGuid((await fixture.Sql.RowAsync("SELECT customer_id FROM bookings WHERE id = @byStaff", new { byStaff }))!["customer_id"]));
    }

    [Fact]
    public async Task Signing_in_is_required()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await fixture.Api.CreateClient().PostJsonAsync("/v1/bookings", Body(shop, shop.StaffA, On(Day, 10))),
            HttpStatusCode.Unauthorized, "Unauthenticated");
        await AssertNothingWrittenAsync(shop);
    }

    // ------------------------------------------------------------ Story: on behalf of a customer

    [Fact]
    public async Task Owner_books_on_behalf_of_a_customer_with_any_staff_member()
    {
        var shop = await ShopAsync();

        // Email matched ignoring case, after trimming (users.email collation; Email schema).
        var body = await CreatedAsync(Book(shop.OwnerId, Body(shop, shop.StaffB, On(Day, 10), $"  {shop.CustomerEmail.ToUpperInvariant()} ")));

        var id = Id(body);
        Assert.Equal(shop.CustomerId, body.GetProperty("customer").GetProperty("userId").GetGuid());
        var history = await HistoryAsync(id);
        Assert.Equal(shop.OwnerId, AsGuid(history["actor_user_id"]));
        Assert.Equal("Owner", history["actor_role"]);
        var createdBy = (await BookingCreatedAsync(id)).GetProperty("data").GetProperty("createdBy");
        Assert.Equal(shop.OwnerId, createdBy.GetProperty("userId").GetGuid());
        Assert.Equal("Owner", createdBy.GetProperty("role").GetString());
        Assert.Equal(shop.CustomerId, (await BookingCreatedAsync(id)).GetProperty("data").GetProperty("customerId").GetGuid());
    }

    [Fact]
    public async Task Staff_member_books_one_of_their_own_slots_on_behalf_of_a_customer()
    {
        var shop = await ShopAsync();

        var id = Id(await CreatedAsync(Book(shop.UserA, Body(shop, shop.StaffA, On(Day, 10), shop.CustomerEmail))));

        var history = await HistoryAsync(id);
        Assert.Equal(shop.UserA, AsGuid(history["actor_user_id"]));
        Assert.Equal("Staff", history["actor_role"]);
        Assert.Equal("Staff", (await BookingCreatedAsync(id)).GetProperty("data").GetProperty("createdBy").GetProperty("role").GetString());
    }

    [Fact]
    public async Task An_owner_who_is_also_the_staff_member_acts_as_Owner()
    {
        var shop = await ShopAsync();
        var ownerStaff = await fixture.Sql.InsertStaffAsync(shop.BusinessId, shop.OwnerId);
        await fixture.Sql.AssignStaffToServiceAsync(ownerStaff, shop.ServiceId);
        await AvailableEveryDayAsync(ownerStaff);

        var id = Id(await CreatedAsync(Book(shop.OwnerId, Body(shop, ownerStaff, On(Day, 10), shop.CustomerEmail))));

        Assert.Equal("Owner", (await HistoryAsync(id))["actor_role"]);
    }

    [Fact]
    public async Task An_account_without_a_password_can_be_booked_for()
    {
        var shop = await ShopAsync();
        var email = ApiFixture.NewEmail();
        var invited = await fixture.Sql.InsertUserAsync(email); // no password yet

        var body = await CreatedAsync(Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 10), email)));

        Assert.Equal(invited, body.GetProperty("customer").GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task An_email_without_an_account_is_CustomerNotFound_and_nothing_changes()
    {
        var shop = await ShopAsync();

        var error = await Json.ErrorAsync(await Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 10), ApiFixture.NewEmail())),
            HttpStatusCode.UnprocessableEntity, "CustomerNotFound");

        Assert.Equal(JsonValueKind.Null, error.GetProperty("rule").ValueKind);
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task Staff_cannot_book_a_customer_with_another_staff_member()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await Book(shop.UserA, Body(shop, shop.StaffB, On(Day, 10), shop.CustomerEmail)), HttpStatusCode.Forbidden, "Forbidden");

        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task Customers_removed_staff_and_outsiders_cannot_book_on_behalf()
    {
        var shop = await ShopAsync();
        var removedUser = await fixture.Sql.InsertUserAsync();
        var removed = await fixture.Sql.InsertStaffAsync(shop.BusinessId, removedUser);
        await fixture.Sql.AssignStaffToServiceAsync(removed, shop.ServiceId);
        await AvailableEveryDayAsync(removed);
        await fixture.Sql.RemoveStaffAsync(removed);
        var otherOwner = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertBusinessAsync(otherOwner);
        var someone = await fixture.Sql.InsertUserAsync();

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10), ApiFixture.NewEmail())), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await Book(removedUser, Body(shop, removed, On(Day, 10), shop.CustomerEmail)), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await Book(otherOwner, Body(shop, shop.StaffA, On(Day, 10), shop.CustomerEmail)), HttpStatusCode.Forbidden, "Forbidden");
        // Even naming the caller's own email is booking on behalf.
        var ownEmail = Convert.ToString(await fixture.Sql.ScalarAsync("SELECT email FROM users WHERE id = @someone", new { someone }))!;
        await Json.ErrorAsync(await Book(someone, Body(shop, shop.StaffA, On(Day, 10), ownEmail)), HttpStatusCode.Forbidden, "Forbidden");
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task Forbidden_comes_before_CustomerNotFound()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await Book(shop.UserA, Body(shop, shop.StaffB, On(Day, 10), ApiFixture.NewEmail())), HttpStatusCode.Forbidden, "Forbidden");
    }

    // ------------------------------------------------------------ R3: minimum notice and its override

    [Fact]
    public async Task R3_a_start_inside_the_minimum_notice_period_is_MinimumNotice()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "min_notice_minutes = 5760"); // 4 days: Day is inside it

        var error = await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10))),
            HttpStatusCode.UnprocessableEntity, "MinimumNotice");

        Assert.Equal("R3", error.GetProperty("rule").GetString());
        Assert.Equal(5760, error.GetProperty("details").GetProperty("minNoticeMinutes").GetInt32());
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task R3_the_notice_boundary_is_inclusive()
    {
        var shop = await ShopAsync();
        // Exactly the notice period before the start is allowed; one minute more is not. The frozen clock has
        // whole seconds, so it is moved forward until the gap to the start is whole minutes.
        var start = On(Day, 10); // fixed before moving the clock, which could cross midnight
        var seconds = (int)(start - fixture.Now).TotalSeconds % 60;
        fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
        try
        {
            var minutes = (int)(start - fixture.Now).TotalMinutes;
            Assert.Equal(start, fixture.Now.AddMinutes(minutes));

            await SetBusinessAsync(shop, $"min_notice_minutes = {minutes + 1}");
            await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, start)), HttpStatusCode.UnprocessableEntity, "MinimumNotice");

            await SetBusinessAsync(shop, $"min_notice_minutes = {minutes}");
            await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, start)));
        }
        finally
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(-seconds));
        }
    }

    [Fact]
    public async Task R3_owner_overrides_the_notice_for_any_staff_member_and_history_records_it()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "min_notice_minutes = 5760");

        var id = Id(await CreatedAsync(Book(shop.OwnerId, Body(shop, shop.StaffB, On(Day, 10), shop.CustomerEmail, overrideNotice: true))));

        var history = await HistoryAsync(id);
        Assert.True(Convert.ToBoolean(history["minimum_notice_overridden"]));
        Assert.Equal(shop.OwnerId, AsGuid(history["actor_user_id"]));
        Assert.Equal("Owner", history["actor_role"]);
    }

    [Fact]
    public async Task R3_staff_override_the_notice_for_their_own_slot_even_when_booking_for_themselves()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "min_notice_minutes = 5760");

        var id = Id(await CreatedAsync(Book(shop.UserA, Body(shop, shop.StaffA, On(Day, 10), overrideNotice: true))));

        var history = await HistoryAsync(id);
        Assert.True(Convert.ToBoolean(history["minimum_notice_overridden"]));
        Assert.Equal("Staff", history["actor_role"]); // the override is a staff privilege (decided 2026-11-20)
        Assert.Equal(shop.UserA, AsGuid((await fixture.Sql.RowAsync("SELECT customer_id FROM bookings WHERE id = @id", new { id }))!["customer_id"]));
    }

    [Fact]
    public async Task R3_staff_cannot_override_for_another_staff_member_nor_customers_at_all()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "min_notice_minutes = 5760");

        await Json.ErrorAsync(await Book(shop.UserA, Body(shop, shop.StaffB, On(Day, 10), overrideNotice: true)), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10), overrideNotice: true)), HttpStatusCode.Forbidden, "Forbidden");
        // Even when the override would not be needed, asking for it needs the permission.
        await SetBusinessAsync(shop, "min_notice_minutes = 0");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10), overrideNotice: true)), HttpStatusCode.Forbidden, "Forbidden");
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task R3_an_override_that_was_not_needed_is_not_recorded()
    {
        var shop = await ShopAsync();

        var onBehalf = Id(await CreatedAsync(Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 9), shop.CustomerEmail, overrideNotice: true))));
        var forSelf = Id(await CreatedAsync(Book(shop.UserA, Body(shop, shop.StaffA, On(Day, 10, 30), overrideNotice: true))));

        Assert.False(Convert.ToBoolean((await HistoryAsync(onBehalf))["minimum_notice_overridden"]));
        Assert.Equal("Owner", (await HistoryAsync(onBehalf))["actor_role"]);
        var self = await HistoryAsync(forSelf);
        Assert.False(Convert.ToBoolean(self["minimum_notice_overridden"]));
        Assert.Equal("Customer", self["actor_role"]); // no staff privilege was used
    }

    [Fact]
    public async Task R3_the_override_does_not_allow_a_start_in_the_past()
    {
        var shop = await ShopAsync();
        var yesterday = DateOnly.FromDateTime(fixture.Now).AddDays(-1);

        await Json.ErrorAsync(await Book(shop.OwnerId, Body(shop, shop.StaffA, On(yesterday, 10), shop.CustomerEmail, overrideNotice: true)),
            HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task R3_the_override_does_not_allow_a_conflict()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "min_notice_minutes = 5760");
        await CreatedAsync(Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 10), shop.CustomerEmail, overrideNotice: true)));

        await Json.ErrorAsync(await Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 10, 30), shop.CustomerEmail, overrideNotice: true)),
            HttpStatusCode.Conflict, "SlotConflict");
        Assert.Equal(1, await BookingCountAsync(shop));
    }

    [Fact]
    public async Task R3_the_override_does_not_allow_a_start_beyond_the_advance_window()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day.AddDays(25), 10), shop.CustomerEmail, overrideNotice: true)),
            HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
    }

    // ------------------------------------------------------------ R8: the start must be offered

    [Theory]
    [InlineData(9, 10)]  // not on a step from the block start
    [InlineData(8, 30)]  // before availability
    [InlineData(11, 30)] // would end at 12:30, after the block (availability bounds the appointment)
    [InlineData(12, 0)]  // after availability
    public async Task R8_a_start_slot_generation_would_not_offer_is_SlotUnavailable(int hour, int minute)
    {
        var shop = await ShopAsync();

        var error = await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, hour, minute))),
            HttpStatusCode.UnprocessableEntity, "SlotUnavailable");

        Assert.Equal("R8", error.GetProperty("rule").GetString());
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task R8_fractional_seconds_off_a_slot_start_are_SlotUnavailable()
    {
        var shop = await ShopAsync();
        var body = Body(shop, shop.StaffA, On(Day, 10));
        body["startUtc"] = $"{Day:yyyy-MM-dd}T10:00:00.5Z";

        await Json.ErrorAsync(await Book(shop.CustomerId, body), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
    }

    [Fact]
    public async Task R8_availability_buffers_and_steps_follow_slot_generation()
    {
        var shop = await ShopAsync();
        // The first slot of a block is offered although its before-buffer starts before availability.
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9))));
        // The last: 11:00-12:00 ends exactly at the block end.
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffB, On(Day, 11))));
    }

    [Fact]
    public async Task R8_a_date_exception_replaces_the_weekly_availability()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staff, @date, '14:15', '16:00')",
            new { id = Guid.NewGuid(), staff = shop.StaffA, date = Day.ToDateTime(TimeOnly.MinValue) });

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 14, 30))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 14, 45)))); // steps from 14:15
    }

    [Fact]
    public async Task R8_time_off_overlapping_the_blocked_interval_is_SlotUnavailable_but_touching_is_fine()
    {
        var shop = await ShopAsync();
        // 10:00 blocks 09:45-11:15. Time off 11:10-11:20 overlaps only the after-buffer.
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO time_off (id, staff_id, start_utc, end_utc, created_at) VALUES (@id, @staff, @start, @end, @now)",
            new { id = Guid.NewGuid(), staff = shop.StaffA, start = On(Day, 11, 10), end = On(Day, 11, 20), now = fixture.Now });

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9)))); // blocks 08:45-10:15
    }

    [Fact]
    public async Task R8_beyond_the_advance_window_or_in_the_past_is_SlotUnavailable()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day.AddDays(25), 10))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day.AddDays(-5), 10))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, new DateTime(1, 1, 1, 10, 0, 0, DateTimeKind.Utc))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, new DateTime(9999, 12, 31, 10, 0, 0, DateTimeKind.Utc))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task SlotUnavailable_wins_over_MinimumNotice_for_a_start_that_would_never_be_offered()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "min_notice_minutes = 5760");

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9, 10))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
    }

    [Fact]
    public async Task MinimumNotice_wins_over_SlotConflict()
    {
        var shop = await ShopAsync();
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10))));
        await SetBusinessAsync(shop, "min_notice_minutes = 5760");

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10, 30))), HttpStatusCode.UnprocessableEntity, "MinimumNotice");
    }

    // ------------------------------------------------------------ Staff and service

    [Fact]
    public async Task A_removed_staff_member_is_SlotUnavailable_and_an_unassigned_one_StaffNotAssignedToService()
    {
        var shop = await ShopAsync();
        await fixture.Sql.RemoveStaffAsync(shop.StaffB);
        var unassigned = await fixture.Sql.InsertStaffAsync(shop.BusinessId);
        await AvailableEveryDayAsync(unassigned);

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffB, On(Day, 10))), HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, unassigned, On(Day, 10))), HttpStatusCode.UnprocessableEntity, "StaffNotAssignedToService");
        // Off-slot too: the assignment is checked first.
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, unassigned, On(Day, 9, 10))), HttpStatusCode.UnprocessableEntity, "StaffNotAssignedToService");
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task Unknown_service_or_a_staffId_outside_the_business_is_404()
    {
        var shop = await ShopAsync();
        var otherBusiness = await fixture.Sql.InsertBusinessAsync();
        var otherStaff = await fixture.Sql.InsertStaffAsync(otherBusiness);

        var unknownService = Body(shop, shop.StaffA, On(Day, 10));
        unknownService["serviceId"] = Guid.NewGuid();
        await Json.ErrorAsync(await Book(shop.CustomerId, unknownService), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, otherStaff, On(Day, 10))), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, Guid.NewGuid(), On(Day, 10))), HttpStatusCode.NotFound, "NotFound");
        // 404 before 403.
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, otherStaff, On(Day, 10), shop.CustomerEmail)), HttpStatusCode.NotFound, "NotFound");
    }

    // ------------------------------------------------------------ Validation

    [Theory]
    [InlineData("""{"staffId":"{staff}","startUtc":"{start}"}""", "serviceId")]
    [InlineData("""{"serviceId":"{service}","startUtc":"{start}"}""", "staffId")]
    [InlineData("""{"serviceId":"{service}","staffId":"{staff}"}""", "startUtc")]
    [InlineData("""{"serviceId":"{service}","staffId":"{staff}","startUtc":"2026-12-01T10:00:00+00:00"}""", "startUtc")]
    [InlineData("""{"serviceId":"{service}","staffId":"{staff}","startUtc":"2026-12-01T10:00:00"}""", "startUtc")]
    [InlineData("""{"serviceId":"{service}","staffId":"{staff}","startUtc":"{start}","customerEmail":"not-an-email"}""", "customerEmail")]
    [InlineData("""{"serviceId":"{service}","staffId":"{staff}","startUtc":"{start}","confirm":{"affectedBookingIds":[]}}""", "confirm")]
    [InlineData("""{"serviceId":"{service}","staffId":"{staff}","startUtc":"{start}","colour":"red"}""", "")] // unknown property: the field name is T30's concern
    [InlineData("""{"serviceId":"nope","staffId":"{staff}","startUtc":"{start}"}""", "serviceId")]
    public async Task Invalid_bodies_are_ValidationFailed_naming_the_field(string template, string field)
    {
        var shop = await ShopAsync();
        var json = template.Replace("{service}", shop.ServiceId.ToString()).Replace("{staff}", shop.StaffA.ToString())
            .Replace("{start}", Iso(On(Day, 10)));

        var error = await Json.ErrorAsync(await BookRaw(shop.CustomerId, json), HttpStatusCode.BadRequest, "ValidationFailed");

        if (field != "") Assert.Contains(Json.FieldNames(error), name => name.StartsWith(field, StringComparison.Ordinal));
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task Confirm_is_accepted_and_ignored_until_T15()
    {
        var shop = await ShopAsync();
        var body = Body(shop, shop.StaffA, On(Day, 10));
        body["confirm"] = new { affectedBookingIds = new[] { Guid.NewGuid() } };

        await CreatedAsync(Book(shop.CustomerId, body));
    }

    // ------------------------------------------------------------ R1 and R7: conflicts

    [Fact]
    public async Task R1_R7_overlapping_a_Confirmed_booking_buffers_included_is_SlotConflict_with_only_the_requested_slot()
    {
        var shop = await ShopAsync();
        var first = Id(await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9))))); // blocks 08:45-10:15
        var other = await fixture.Sql.InsertUserAsync();

        // 10:00 blocks 09:45-11:15: only the buffers overlap.
        var error = await Json.ErrorAsync(await Book(other, Body(shop, shop.StaffA, On(Day, 10))), HttpStatusCode.Conflict, "SlotConflict");

        Assert.Equal("R7", error.GetProperty("rule").GetString());
        var details = error.GetProperty("details");
        Assert.Equal(["endUtc", "staffId", "startUtc"], details.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(shop.StaffA, details.GetProperty("staffId").GetGuid());
        Assert.Equal(Iso(On(Day, 10)), details.GetProperty("startUtc").GetString());
        Assert.Equal(Iso(On(Day, 11)), details.GetProperty("endUtc").GetString());
        var text = error.ToString();
        Assert.DoesNotContain(first.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shop.CustomerId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shop.CustomerEmail, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await BookingCountAsync(shop));
    }

    [Fact]
    public async Task R1_touching_blocked_intervals_another_staff_member_and_cancelled_bookings_do_not_conflict()
    {
        var shop = await ShopAsync();
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9))));      // blocks 08:45-10:15
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10, 30)))); // blocks 10:15-11:45
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffB, On(Day, 10))));     // another staff member

        var cancelled = Infrastructure.Sql.BookingParameters(new(shop.BusinessId, shop.StaffB, shop.ServiceId, shop.CustomerId));
        cancelled["start"] = On(Day.AddDays(1), 10);
        cancelled["end"] = On(Day.AddDays(1), 11);
        cancelled["blockedStart"] = On(Day.AddDays(1), 9, 45);
        cancelled["blockedEnd"] = On(Day.AddDays(1), 11, 15);
        cancelled["status"] = "Cancelled";
        cancelled["cancelledAt"] = fixture.Now;
        cancelled["cancelledBy"] = shop.CustomerId;
        cancelled["reason"] = "CustomerCancelled";
        await fixture.Sql.ExecuteAsync(Infrastructure.Sql.InsertBookingSql, cancelled);

        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffB, On(Day.AddDays(1), 10))));
    }

    [Fact]
    public async Task R1_a_stored_blocked_interval_conflicts_even_if_the_service_buffers_shrank()
    {
        var shop = await ShopAsync();
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9)))); // blocks 08:45-10:15
        await fixture.Sql.ExecuteAsync("UPDATE services SET buffer_before_minutes = 0, buffer_after_minutes = 0 WHERE id = @id", new { id = shop.ServiceId });

        // 10:00 no longer has a before-buffer, but the earlier booking's after-buffer still blocks until 10:15 (ADR 0004).
        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10))), HttpStatusCode.Conflict, "SlotConflict");
    }

    [Fact]
    public async Task R7_simultaneous_overlapping_requests_give_exactly_one_success_and_one_409()
    {
        var shop = await ShopAsync();
        var other = await fixture.Sql.InsertUserAsync();
        Task<HttpResponseMessage> first, second;

        // Hold the staff lock until both requests are waiting for it (ADR 0002), so they truly overlap.
        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            first = Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 9, 30)));
            second = Book(other, Body(shop, shop.StaffA, On(Day, 10)));
            await fixture.Sql.WaitForLockWaitsAsync("staff", 2);
            Assert.False(first.IsCompleted || second.IsCompleted);
        }

        var statuses = (await Task.WhenAll(first, second)).Select(r => r.StatusCode).Order().ToList();
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], statuses);
        var loser = (await first).StatusCode == HttpStatusCode.Conflict ? await first : await second;
        var details = (await Json.ErrorAsync(loser, HttpStatusCode.Conflict, "SlotConflict")).GetProperty("details");
        Assert.Equal(shop.StaffA, details.GetProperty("staffId").GetGuid());
        Assert.Equal(1, await BookingCountAsync(shop));
    }

    [Fact]
    public async Task R7_racing_requests_never_double_book()
    {
        var shop = await ShopAsync();
        var customers = new List<Guid>();
        for (var i = 0; i < 6; i++) customers.Add(await fixture.Sql.InsertUserAsync());

        // Six customers at once for overlapping starts with one staff member; no lock held by the test.
        var starts = new[] { On(Day, 9, 30), On(Day, 10), On(Day, 9, 30), On(Day, 10, 30), On(Day, 10), On(Day, 9, 30) };
        var responses = await Task.WhenAll(customers.Select((c, i) => Book(c, Body(shop, shop.StaffA, starts[i]))));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        var booked = await CountAsync("SELECT COUNT(*) FROM bookings WHERE staff_id = @staff", new { staff = shop.StaffA });
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.Created), booked);
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM bookings a JOIN bookings b ON a.staff_id = b.staff_id AND a.id < b.id " +
            "AND a.blocked_start_utc < b.blocked_end_utc AND b.blocked_start_utc < a.blocked_end_utc WHERE a.staff_id = @staff",
            new { staff = shop.StaffA }));
    }

    // ------------------------------------------------------------ ADR 0002: locks

    [Fact]
    public async Task ADR0002_the_customer_user_row_is_taken_before_the_staff_row()
    {
        var shop = await ShopAsync();
        Task<HttpResponseMessage> book;

        await using (var held = await fixture.Sql.LockUserRowAsync(shop.CustomerId))
        {
            book = Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10)));
            await fixture.Sql.WaitForLockWaitAsync("users");
            // While the booking waits for the user row, it holds no staff lock.
            await using var probe = await fixture.Sql.BeginTransactionAsync();
            await probe.ExecuteAsync("SELECT id FROM staff WHERE id = @id FOR UPDATE NOWAIT", new { id = shop.StaffA });
        }

        await CreatedAsync(book);
    }

    [Fact]
    public async Task ADR0002_changes_committed_while_waiting_for_the_staff_lock_are_seen()
    {
        var shop = await ShopAsync();
        Task<HttpResponseMessage> blockedByTimeOff, onRemovedStaff;

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffA))
        {
            blockedByTimeOff = Book(shop.CustomerId, Body(shop, shop.StaffA, On(Day, 10)));
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync(
                "INSERT INTO time_off (id, staff_id, start_utc, end_utc, created_at) VALUES (@id, @staff, @start, @end, @now)",
                new { id = Guid.NewGuid(), staff = shop.StaffA, start = On(Day, 10), end = On(Day, 10, 30), now = fixture.Now });
            await held.CommitAsync();
        }
        await Json.ErrorAsync(await blockedByTimeOff, HttpStatusCode.UnprocessableEntity, "SlotUnavailable");

        await using (var held = await fixture.Sql.LockRowAsync("staff", shop.StaffB))
        {
            onRemovedStaff = Book(shop.UserB, Body(shop, shop.StaffB, On(Day, 10), shop.CustomerEmail));
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync("UPDATE staff SET removed_at = @now WHERE id = @id", new { id = shop.StaffB, now = fixture.Now });
            await held.CommitAsync();
        }
        // The staff member was removed while waiting, so they may no longer book on behalf.
        await Json.ErrorAsync(await onRemovedStaff, HttpStatusCode.Forbidden, "Forbidden");
        await AssertNothingWrittenAsync(shop);
    }

    // ------------------------------------------------------------ Time zones (R2, R8)

    private Task InsertExceptionAsync(Guid staffId, DateOnly date, string start, string end) => fixture.Sql.ExecuteAsync(
        "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staffId, @date, @start, @end)",
        new { id = Guid.NewGuid(), staffId, date = date.ToDateTime(TimeOnly.MinValue), start, end });

    private static DateTime LocalToUtc(DateTimeZone zone, DateOnly date, int hour, int minute = 0) =>
        zone.AtStrictly(LocalDate.FromDateOnly(date).At(new LocalTime(hour, minute))).ToDateTimeUtc();

    /// <summary>The next transition after now where the wall offset goes up (spring forward) or down (fall back).</summary>
    private Instant NextTransition(DateTimeZone zone, bool springForward)
    {
        var interval = zone.GetZoneInterval(Instant.FromDateTimeUtc(fixture.Now));
        while (true)
        {
            var next = zone.GetZoneInterval(interval.End);
            if (next.WallOffset > interval.WallOffset == springForward) return interval.End;
            interval = next;
        }
    }

    [Fact]
    public async Task R2_R8_availability_is_read_for_the_start_local_date_not_its_UTC_date()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "time_zone = 'America/Los_Angeles'");
        var zone = DateTimeZoneProviders.Tzdb["America/Los_Angeles"];
        // Only Day (local) has evening hours; 17:00 there is already the next day in UTC, where the weekly
        // 09:00-12:00 applies instead.
        await InsertExceptionAsync(shop.StaffA, Day, "16:00", "20:00");
        var start = LocalToUtc(zone, Day, 17);
        Assert.NotEqual(Day, DateOnly.FromDateTime(start));

        var body = await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, start)));

        Assert.Equal("America/Los_Angeles", body.GetProperty("businessTimeZone").GetString());
        Assert.Equal(Iso(start), body.GetProperty("startUtc").GetString());
    }

    [Fact]
    public async Task R8_a_far_future_start_in_a_zone_ahead_of_UTC_is_SlotUnavailable_not_an_error()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "time_zone = 'Pacific/Kiritimati'"); // UTC+14: 9999-12-31 12:00Z is local year 10000

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, new DateTime(9999, 12, 31, 12, 0, 0, DateTimeKind.Utc))),
            HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
    }

    [Fact]
    public async Task R2_a_booking_across_spring_forward_lasts_its_duration_in_elapsed_time()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "time_zone = 'America/New_York', max_advance_days = 3650");
        await fixture.Sql.ExecuteAsync("UPDATE services SET duration_minutes = 120 WHERE id = @id", new { id = shop.ServiceId });
        var zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        var date = NextTransition(zone, springForward: true).InZone(zone).Date.ToDateOnly();
        await InsertExceptionAsync(shop.StaffA, date, "00:00", "06:00");

        // R2's example: a 2-hour booking at 01:00 on a spring-forward night ends at 04:00 local.
        var body = await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, LocalToUtc(zone, date, 1))));

        var start = body.GetProperty("startUtc").GetDateTime().ToUniversalTime();
        var end = body.GetProperty("endUtc").GetDateTime().ToUniversalTime();
        Assert.Equal(TimeSpan.FromHours(2), end - start);
        Assert.Equal(new LocalTime(4, 0), Instant.FromDateTimeUtc(end).InZone(zone).TimeOfDay);
    }

    [Fact]
    public async Task R2_only_the_first_occurrence_of_a_repeated_fall_back_time_can_be_booked()
    {
        var shop = await ShopAsync();
        await SetBusinessAsync(shop, "time_zone = 'America/New_York', max_advance_days = 3650");
        var zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        var date = NextTransition(zone, springForward: false).InZone(zone).Date.ToDateOnly();
        await InsertExceptionAsync(shop.StaffA, date, "00:00", "06:00");
        var oneAm = zone.MapLocal(LocalDate.FromDateOnly(date).At(new LocalTime(1, 0)));
        Assert.Equal(2, oneAm.Count);

        await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, oneAm.Last().ToDateTimeUtc())),
            HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
        await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, oneAm.First().ToDateTimeUtc())));
    }

    [Fact]
    public async Task R8_the_advance_window_is_inclusive_of_its_end()
    {
        var shop = await ShopAsync();
        // Move the clock to a whole minute, so now + 21 days can be a slot start with a 1-minute step. The
        // business gets a fixed-offset zone that puts that instant near local noon, inside 00:00-23:59.
        var seconds = (60 - fixture.Now.Second) % 60;
        fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
        try
        {
            var windowEnd = fixture.Now.AddDays(21);
            var offset = 12 - windowEnd.Hour; // hours east of UTC; Etc/GMT names have the sign reversed
            var zoneName = offset == 0 ? "Etc/GMT" : offset > 0 ? $"Etc/GMT-{offset}" : $"Etc/GMT+{-offset}";
            await SetBusinessAsync(shop, $"time_zone = '{zoneName}', slot_step_minutes = 1");
            await fixture.Sql.ExecuteAsync("UPDATE availability_rules SET start_local = '00:00', end_local = '23:59' WHERE staff_id = @staff",
                new { staff = shop.StaffA });

            await Json.ErrorAsync(await Book(shop.CustomerId, Body(shop, shop.StaffA, windowEnd.AddMinutes(1))),
                HttpStatusCode.UnprocessableEntity, "SlotUnavailable");
            await CreatedAsync(Book(shop.CustomerId, Body(shop, shop.StaffA, windowEnd)));
        }
        finally
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(-seconds));
        }
    }

    // ------------------------------------------------------------ User rows that vanish while waiting

    [Fact]
    public async Task A_customer_deleted_while_the_booking_waits_for_their_row_is_CustomerNotFound()
    {
        var shop = await ShopAsync();
        var email = ApiFixture.NewEmail();
        var customer = await fixture.Sql.InsertUserAsync(email);
        Task<HttpResponseMessage> book;

        await using (var held = await fixture.Sql.LockUserRowAsync(customer))
        {
            book = Book(shop.OwnerId, Body(shop, shop.StaffA, On(Day, 10), email));
            await fixture.Sql.WaitForLockWaitAsync("users");
            await held.ExecuteAsync("DELETE FROM users WHERE id = @customer", new { customer });
            await held.CommitAsync();
        }

        await Json.ErrorAsync(await book, HttpStatusCode.UnprocessableEntity, "CustomerNotFound");
        await AssertNothingWrittenAsync(shop);
    }

    [Fact]
    public async Task A_caller_deleted_while_the_booking_waits_for_their_row_is_Unauthenticated()
    {
        var shop = await ShopAsync();
        var caller = await fixture.Sql.InsertUserAsync();
        Task<HttpResponseMessage> book;

        await using (var held = await fixture.Sql.LockUserRowAsync(caller))
        {
            book = Book(caller, Body(shop, shop.StaffA, On(Day, 10)));
            await fixture.Sql.WaitForLockWaitAsync("users");
            await held.ExecuteAsync("DELETE FROM users WHERE id = @caller", new { caller });
            await held.CommitAsync();
        }

        await Json.ErrorAsync(await book, HttpStatusCode.Unauthorized, "Unauthenticated");
        await AssertNothingWrittenAsync(shop);
    }
}
