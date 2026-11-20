using System.Globalization;
using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using NodaTime;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T13: GET /v1/services/{serviceId}/slots (story "Customer views open slots for a service and date
/// range"). The rules themselves (DST, steps, boundaries) are covered without the database in
/// SlotGeneratorTests; these tests cover access, validation and what is read from the database.
/// Decisions 2026-11-20: a staffId outside the business is 404, a removed or unassigned member gives
/// no slots; ordered by start then staffId. The business time zone is UTC here unless a test says
/// otherwise, so local and UTC times coincide. Every staff member is available 09:00-12:00 every day,
/// and the service lasts 60 minutes with 15-minute buffers, so each day offers 09:00 to 11:00.
/// </summary>
public class SlotsTests(FrozenClockApiFixture fixture) : IClassFixture<FrozenClockApiFixture>
{
    private static readonly string[] DayStarts = ["09:00", "09:30", "10:00", "10:30", "11:00"];

    private sealed record Shop(
        Guid OwnerId, Guid BusinessId, Guid ServiceId, Guid StaffA, Guid UserA, Guid StaffB, Guid UserB,
        Guid RemovedStaff, Guid RemovedUser, Guid UnassignedStaff, Guid CustomerId);

    /// <summary>A date three days ahead: inside the default 2-hour notice and 21-day window.</summary>
    private DateOnly Day => DateOnly.FromDateTime(fixture.Now).AddDays(3);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string At(DateOnly d, string time) => $"{Iso(d)}T{time}:00Z";

    private async Task<Shop> ShopAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync();
        var business = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'UTC' WHERE id = @business", new { business });
        var service = await fixture.Sql.InsertServiceAsync(business); // 60 minutes, 15-minute buffers

        async Task<(Guid Staff, Guid User)> StaffAsync(bool assigned)
        {
            var user = await fixture.Sql.InsertUserAsync();
            var staff = await fixture.Sql.InsertStaffAsync(business, user);
            if (assigned) await fixture.Sql.AssignStaffToServiceAsync(staff, service);
            await AvailableEveryDayAsync(staff, "09:00", "12:00");
            return (staff, user);
        }

        var a = await StaffAsync(true);
        var b = await StaffAsync(true);
        var removed = await StaffAsync(true);
        await fixture.Sql.RemoveStaffAsync(removed.Staff);
        var unassigned = await StaffAsync(false);
        return new Shop(owner, business, service, a.Staff, a.User, b.Staff, b.User, removed.Staff, removed.User,
            unassigned.Staff, await fixture.Sql.InsertUserAsync());
    }

    private async Task AvailableEveryDayAsync(Guid staffId, string start, string end)
    {
        foreach (var day in Enum.GetNames<DayOfWeek>())
        {
            await fixture.Sql.ExecuteAsync(
                "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staffId, @day, @start, @end)",
                new { id = Guid.NewGuid(), staffId, day, start, end });
        }
    }

    private Task<HttpResponseMessage> Get(Guid callerId, Guid serviceId, string query) =>
        fixture.Api.ClientFor(callerId).GetAsync($"/v1/services/{serviceId}/slots?{query}");

    private Task<HttpResponseMessage> GetDay(Guid callerId, Shop shop, string extra = "") =>
        Get(callerId, shop.ServiceId, $"from={Iso(Day)}&to={Iso(Day)}{extra}");

    private static List<(Guid Staff, string Start)> Slots(JsonElement body) =>
        body.GetProperty("slots").EnumerateArray()
            .Select(s => (s.GetProperty("staffId").GetGuid(), s.GetProperty("startUtc").GetString()!)).ToList();

    private static List<string> StartsFor(JsonElement body, Guid staffId) =>
        Slots(body).Where(s => s.Staff == staffId).Select(s => s.Start).ToList();

    private List<string> DayAt(params string[] times) => times.Select(t => At(Day, t)).ToList();

    private async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request) => await Json.ReadAsync(await request, HttpStatusCode.OK);

    // ------------------------------------------------------------ Story and response

    [Fact]
    public async Task Customer_views_open_slots_for_all_active_staff_assigned_to_the_service()
    {
        var shop = await ShopAsync();

        var body = await OkAsync(GetDay(shop.CustomerId, shop));

        Assert.Equal(["businessTimeZone", "slots"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("UTC", body.GetProperty("businessTimeZone").GetString());
        var first = body.GetProperty("slots")[0];
        Assert.Equal(["endUtc", "staffId", "startUtc"], first.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(At(Day, "10:00"), first.GetProperty("endUtc").GetString());

        // Ordered by start, then staffId (decided 2026-11-20); removed and unassigned staff get no slots.
        var ordered = new[] { shop.StaffA, shop.StaffB }.OrderBy(s => s.ToString(), StringComparer.Ordinal).ToArray();
        var expected = DayStarts.SelectMany(t => ordered.Select(s => (s, At(Day, t)))).ToList();
        Assert.Equal(expected, Slots(body));
    }

    [Fact]
    public async Task Staff_owner_and_admin_also_view_slots()
    {
        var shop = await ShopAsync();

        foreach (var caller in new[] { shop.OwnerId, shop.UserA, shop.RemovedUser, await fixture.Sql.InsertAdminUserAsync() })
        {
            Assert.Equal(10, Slots(await OkAsync(GetDay(caller, shop))).Count);
        }
    }

    [Fact]
    public async Task R8_slots_cover_every_date_in_the_range_both_inclusive()
    {
        var shop = await ShopAsync();

        var body = await OkAsync(Get(shop.CustomerId, shop.ServiceId, $"from={Iso(Day)}&to={Iso(Day.AddDays(2))}&staffId={shop.StaffA}"));

        Assert.Equal(new[] { Day, Day.AddDays(1), Day.AddDays(2) }.SelectMany(d => DayStarts.Select(t => At(d, t))), StartsFor(body, shop.StaffA));
    }

    [Fact]
    public async Task Unknown_service_is_404_and_signing_in_is_required()
    {
        var shop = await ShopAsync();

        await Json.ErrorAsync(await Get(shop.CustomerId, Guid.NewGuid(), $"from={Iso(Day)}&to={Iso(Day)}"), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync($"/v1/services/{shop.ServiceId}/slots?from={Iso(Day)}&to={Iso(Day)}"),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    // ------------------------------------------------------------ staffId

    [Fact]
    public async Task StaffId_limits_slots_to_that_staff_member()
    {
        var shop = await ShopAsync();

        var body = await OkAsync(GetDay(shop.CustomerId, shop, $"&staffId={shop.StaffB}"));

        Assert.Equal(DayAt(DayStarts), StartsFor(body, shop.StaffB));
        Assert.All(Slots(body), s => Assert.Equal(shop.StaffB, s.Staff));
    }

    [Fact]
    public async Task StaffId_of_a_removed_or_unassigned_member_gives_no_slots()
    {
        var shop = await ShopAsync();

        Assert.Empty(Slots(await OkAsync(GetDay(shop.CustomerId, shop, $"&staffId={shop.RemovedStaff}"))));
        Assert.Empty(Slots(await OkAsync(GetDay(shop.CustomerId, shop, $"&staffId={shop.UnassignedStaff}"))));
    }

    [Fact]
    public async Task StaffId_outside_the_business_is_404()
    {
        var shop = await ShopAsync();
        var otherBusiness = await fixture.Sql.InsertBusinessAsync();
        var otherStaff = await fixture.Sql.InsertStaffAsync(otherBusiness);

        await Json.ErrorAsync(await GetDay(shop.CustomerId, shop, $"&staffId={otherStaff}"), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await GetDay(shop.CustomerId, shop, $"&staffId={Guid.NewGuid()}"), HttpStatusCode.NotFound, "NotFound");
    }

    // ------------------------------------------------------------ Range (ADR 0005) and validation

    [Fact]
    public async Task ADR0005_a_range_of_30_dates_is_allowed_and_31_is_RangeTooLong()
    {
        var shop = await ShopAsync();

        await OkAsync(Get(shop.CustomerId, shop.ServiceId, $"from={Iso(Day)}&to={Iso(Day.AddDays(29))}"));
        var error = await Json.ErrorAsync(await Get(shop.CustomerId, shop.ServiceId, $"from={Iso(Day)}&to={Iso(Day.AddDays(30))}"),
            HttpStatusCode.BadRequest, "RangeTooLong");
        Assert.Equal(30, error.GetProperty("details").GetProperty("maxDays").GetInt32());
        Assert.Contains("30", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ADR0005_ranges_in_the_past_or_beyond_the_advance_window_are_empty_not_errors()
    {
        var shop = await ShopAsync();
        var today = DateOnly.FromDateTime(fixture.Now);

        Assert.Empty(Slots(await OkAsync(Get(shop.CustomerId, shop.ServiceId, $"from={Iso(today.AddDays(-40))}&to={Iso(today.AddDays(-11))}"))));
        Assert.Empty(Slots(await OkAsync(Get(shop.CustomerId, shop.ServiceId, $"from={Iso(today.AddDays(22))}&to={Iso(today.AddDays(51))}"))));
        Assert.Empty(Slots(await OkAsync(Get(shop.CustomerId, shop.ServiceId, "from=0001-01-01&to=0001-01-30"))));
        Assert.Empty(Slots(await OkAsync(Get(shop.CustomerId, shop.ServiceId, "from=9999-12-02&to=9999-12-31"))));
    }

    [Theory]
    [InlineData("to=2026-12-01", "from")]
    [InlineData("from=2026-12-01", "to")]
    [InlineData("from=&to=2026-12-01", "from")]
    [InlineData("from=2026-12-1&to=2026-12-01", "from")]
    [InlineData("from=2026-12-01&to=2026-02-30", "to")]
    [InlineData("from=2026-12-02&to=2026-12-01", "from")]
    [InlineData("from=2026-12-01&to=2026-12-01&staffId=abc", "staffId")]
    [InlineData("from=2026-12-01&to=2026-12-01&ignoreMinimumNotice=maybe", "ignoreMinimumNotice")]
    public async Task Invalid_queries_are_ValidationFailed_naming_the_parameter(string query, string field)
    {
        var shop = await ShopAsync();

        var error = await Json.ErrorAsync(await Get(shop.CustomerId, shop.ServiceId, query), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    // ------------------------------------------------------------ What blocks slots (R1, R8)

    [Fact]
    public async Task R1_a_Confirmed_booking_blocks_by_its_stored_blocked_interval()
    {
        var shop = await ShopAsync();
        var bookingFixture = new Infrastructure.Sql.BookingFixture(shop.BusinessId, shop.StaffA, shop.ServiceId, shop.CustomerId);
        // Booked 11:00-11:30 with 30-minute buffers snapshotted (ADR 0004), so it blocks 10:30-12:00. With the
        // service's current 15-minute buffers, 09:30 (blocking 09:15-10:45) overlaps it and is not offered.
        var parameters = Infrastructure.Sql.BookingParameters(bookingFixture);
        parameters["start"] = Day.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc);
        parameters["end"] = Day.ToDateTime(new TimeOnly(11, 30), DateTimeKind.Utc);
        parameters["blockedStart"] = Day.ToDateTime(new TimeOnly(10, 30), DateTimeKind.Utc);
        parameters["blockedEnd"] = Day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        await fixture.Sql.ExecuteAsync(Infrastructure.Sql.InsertBookingSql, parameters);

        var body = await OkAsync(GetDay(shop.CustomerId, shop));

        Assert.Equal(DayAt("09:00"), StartsFor(body, shop.StaffA));
        Assert.Equal(DayAt(DayStarts), StartsFor(body, shop.StaffB)); // per staff member
    }

    private async Task InsertBookingAsync(Shop shop, Guid serviceId, DateTime start, DateTime end, DateTime blockedStart, DateTime blockedEnd)
    {
        var parameters = Infrastructure.Sql.BookingParameters(new(shop.BusinessId, shop.StaffA, serviceId, shop.CustomerId));
        parameters["start"] = start;
        parameters["end"] = end;
        parameters["blockedStart"] = blockedStart;
        parameters["blockedEnd"] = blockedEnd;
        await fixture.Sql.ExecuteAsync(Infrastructure.Sql.InsertBookingSql, parameters);
    }

    private DateTime On(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    [Fact]
    public async Task R1_a_booking_for_another_service_blocks_the_same_staff_member()
    {
        var shop = await ShopAsync();
        var otherService = await fixture.Sql.InsertServiceAsync(shop.BusinessId, "Colour");
        await fixture.Sql.AssignStaffToServiceAsync(shop.StaffA, otherService);
        await InsertBookingAsync(shop, otherService, On(Day, 11), On(Day, 11, 30), On(Day, 10, 30), On(Day, 12));

        var body = await OkAsync(GetDay(shop.CustomerId, shop));

        Assert.Equal(DayAt("09:00"), StartsFor(body, shop.StaffA));
    }

    [Fact]
    public async Task R1_a_booking_that_started_before_the_range_still_blocks()
    {
        var shop = await ShopAsync();
        // Within the service maxima: 23 h 45 min long, a 23-hour before-buffer and a 1-hour after-buffer, so its
        // blocked interval starts two days before the requested date and ends at 10:15 on it.
        await InsertBookingAsync(shop, shop.ServiceId, On(Day.AddDays(-1), 9, 30), On(Day, 9, 15), On(Day.AddDays(-2), 10, 30), On(Day, 10, 15));

        Assert.Equal(DayAt("10:30", "11:00"), StartsFor(await OkAsync(GetDay(shop.CustomerId, shop)), shop.StaffA));
    }

    private Task InsertTimeOffAsync(Guid staffId, DateTime start, DateTime end) => fixture.Sql.ExecuteAsync(
        "INSERT INTO time_off (id, staff_id, start_utc, end_utc, created_at) VALUES (@id, @staffId, @start, @end, @now)",
        new { id = Guid.NewGuid(), staffId, start, end, now = fixture.Now });

    private Task SetExceptionAsync(Guid staffId, string start, string end) => fixture.Sql.ExecuteAsync(
        "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staffId, @date, @start, @end)",
        new { id = Guid.NewGuid(), staffId, date = Day.ToDateTime(TimeOnly.MinValue), start, end });

    [Fact]
    public async Task R1_R8_a_before_buffer_reaches_back_across_midnight()
    {
        var shop = await ShopAsync();
        await SetExceptionAsync(shop.StaffA, "00:00", "02:00");
        // Ends 5 minutes before midnight: inside the 00:00 slot's 15-minute before-buffer.
        await InsertTimeOffAsync(shop.StaffA, On(Day.AddDays(-1), 23, 50), On(Day.AddDays(-1), 23, 55));

        Assert.Equal(DayAt("00:30", "01:00"), StartsFor(await OkAsync(GetDay(shop.CustomerId, shop)), shop.StaffA));
    }

    [Fact]
    public async Task R1_R8_an_after_buffer_reaches_forward_across_midnight()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE services SET buffer_after_minutes = 60 WHERE id = @id", new { id = shop.ServiceId });
        await SetExceptionAsync(shop.StaffA, "22:00", "23:59");
        // 22:30-23:30 blocks until 00:30, overlapping this; 22:00-23:00 blocks until 00:00, which only touches it.
        await InsertTimeOffAsync(shop.StaffA, On(Day.AddDays(1), 0, 10), On(Day.AddDays(1), 0, 20));

        Assert.Equal(DayAt("22:00"), StartsFor(await OkAsync(GetDay(shop.CustomerId, shop)), shop.StaffA));
    }

    [Fact]
    public async Task R1_a_Cancelled_booking_does_not_block()
    {
        var shop = await ShopAsync();
        var parameters = Infrastructure.Sql.BookingParameters(new(shop.BusinessId, shop.StaffA, shop.ServiceId, shop.CustomerId));
        parameters["start"] = Day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);
        parameters["end"] = Day.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc);
        parameters["blockedStart"] = Day.ToDateTime(new TimeOnly(9, 45), DateTimeKind.Utc);
        parameters["blockedEnd"] = Day.ToDateTime(new TimeOnly(11, 15), DateTimeKind.Utc);
        parameters["status"] = "Cancelled";
        parameters["cancelledAt"] = fixture.Now;
        parameters["cancelledBy"] = shop.CustomerId;
        parameters["reason"] = "CustomerCancelled";
        await fixture.Sql.ExecuteAsync(Infrastructure.Sql.InsertBookingSql, parameters);

        Assert.Equal(DayAt(DayStarts), StartsFor(await OkAsync(GetDay(shop.CustomerId, shop)), shop.StaffA));
    }

    [Fact]
    public async Task R8_time_off_blocks_slots_whose_blocked_interval_overlaps_it()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO time_off (id, staff_id, start_utc, end_utc, created_at) VALUES (@id, @staff, @start, @end, @now)",
            new { id = Guid.NewGuid(), staff = shop.StaffA, start = Day.ToDateTime(new TimeOnly(10, 45)), end = Day.ToDateTime(new TimeOnly(11, 45)), now = fixture.Now });

        var body = await OkAsync(GetDay(shop.CustomerId, shop));

        Assert.Equal(DayAt("09:00", "09:30"), StartsFor(body, shop.StaffA)); // 09:30 blocks until 10:45: touching is fine
    }

    [Fact]
    public async Task R8_time_off_starting_the_day_before_still_blocks()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO time_off (id, staff_id, start_utc, end_utc, created_at) VALUES (@id, @staff, @start, @end, @now)",
            new { id = Guid.NewGuid(), staff = shop.StaffA, start = Day.AddDays(-1).ToDateTime(new TimeOnly(9, 0)), end = Day.ToDateTime(new TimeOnly(10, 15)), now = fixture.Now });

        Assert.Equal(DayAt("10:30", "11:00"), StartsFor(await OkAsync(GetDay(shop.CustomerId, shop)), shop.StaffA));
    }

    [Fact]
    public async Task R8_a_date_exception_replaces_the_weekly_availability()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staff, @date, '14:00', '15:30')",
            new { id = Guid.NewGuid(), staff = shop.StaffA, date = Day.ToDateTime(TimeOnly.MinValue) });
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staff, @date, NULL, NULL)",
            new { id = Guid.NewGuid(), staff = shop.StaffB, date = Day.ToDateTime(TimeOnly.MinValue) });

        var body = await OkAsync(GetDay(shop.CustomerId, shop));

        Assert.Equal(DayAt("14:00", "14:30"), StartsFor(body, shop.StaffA));
        Assert.Empty(StartsFor(body, shop.StaffB));
    }

    [Fact]
    public async Task R8_the_business_slot_step_is_used()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET slot_step_minutes = 60 WHERE id = @id", new { id = shop.BusinessId });

        Assert.Equal(DayAt("09:00", "10:00", "11:00"), StartsFor(await OkAsync(GetDay(shop.CustomerId, shop)), shop.StaffA));
    }

    [Fact]
    public async Task R8_the_business_advance_window_is_used()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET max_advance_days = 2 WHERE id = @id", new { id = shop.BusinessId });

        Assert.Empty(Slots(await OkAsync(GetDay(shop.CustomerId, shop))));
    }

    [Fact]
    public async Task R2_R8_availability_is_in_the_business_time_zone()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'America/New_York' WHERE id = @id", new { id = shop.BusinessId });

        var body = await OkAsync(GetDay(shop.CustomerId, shop, $"&staffId={shop.StaffA}"));

        var zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        var expected = DayStarts.Select(t => LocalDate.FromDateOnly(Day).At(LocalTime.FromTimeOnly(TimeOnly.Parse(t)))
            .InZoneStrictly(zone).ToInstant().ToDateTimeUtc().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).ToList();
        Assert.Equal("America/New_York", body.GetProperty("businessTimeZone").GetString());
        Assert.Equal(expected, StartsFor(body, shop.StaffA));
    }

    [Fact]
    public async Task R2_a_stored_zone_missing_from_tzdb_falls_back_to_the_OS_zone_data()
    {
        // Validation never stored a Windows name, but it is a name the OS knows (via ICU) and tzdb does not,
        // standing in for a zone added to the OS data after the bundled tzdb was built.
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'Eastern Standard Time' WHERE id = @id", new { id = shop.BusinessId });

        var body = await OkAsync(GetDay(shop.CustomerId, shop, $"&staffId={shop.StaffA}"));

        var zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        var expected = DayStarts.Select(t => LocalDate.FromDateOnly(Day).At(LocalTime.FromTimeOnly(TimeOnly.Parse(t)))
            .InZoneStrictly(zone).ToInstant().ToDateTimeUtc().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).ToList();
        Assert.Equal("Eastern Standard Time", body.GetProperty("businessTimeZone").GetString());
        Assert.Equal(expected, StartsFor(body, shop.StaffA));
    }

    [Fact]
    public async Task R2_a_stored_zone_nobody_knows_is_an_internal_error()
    {
        var shop = await ShopAsync();
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'Mars/Olympus_Mons' WHERE id = @id", new { id = shop.BusinessId });

        await Json.ErrorAsync(await GetDay(shop.CustomerId, shop), HttpStatusCode.InternalServerError, "InternalError");
    }

    // ------------------------------------------------------------ R3 minimum notice

    /// <summary>Sets the notice so that it ends between 09:59 and 10:00 on <see cref="Day"/>.</summary>
    private async Task NoticeEndsAtTenAsync(Shop shop)
    {
        var minutes = (int)Math.Floor((Day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc) - fixture.Now).TotalMinutes);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET min_notice_minutes = @minutes WHERE id = @id", new { minutes, id = shop.BusinessId });
    }

    [Fact]
    public async Task R3_slots_inside_the_minimum_notice_period_are_not_offered()
    {
        var shop = await ShopAsync();
        await NoticeEndsAtTenAsync(shop);

        Assert.Equal(DayAt("10:00", "10:30", "11:00"), StartsFor(await OkAsync(GetDay(shop.CustomerId, shop)), shop.StaffA));
    }

    [Fact]
    public async Task R3_the_owner_may_ignore_the_notice_for_any_staff_member()
    {
        var shop = await ShopAsync();
        await NoticeEndsAtTenAsync(shop);

        var all = await OkAsync(GetDay(shop.OwnerId, shop, "&ignoreMinimumNotice=true"));
        var one = await OkAsync(GetDay(shop.OwnerId, shop, $"&staffId={shop.StaffB}&ignoreMinimumNotice=true"));

        Assert.Equal(DayAt(DayStarts), StartsFor(all, shop.StaffA));
        Assert.Equal(DayAt(DayStarts), StartsFor(all, shop.StaffB));
        Assert.Equal(DayAt(DayStarts), StartsFor(one, shop.StaffB));
    }

    [Fact]
    public async Task R3_staff_may_ignore_the_notice_for_their_own_slots()
    {
        var shop = await ShopAsync();
        await NoticeEndsAtTenAsync(shop);

        var body = await OkAsync(GetDay(shop.UserA, shop, $"&staffId={shop.StaffA}&ignoreMinimumNotice=true"));

        Assert.Equal(DayAt(DayStarts), StartsFor(body, shop.StaffA));
    }

    [Fact]
    public async Task R3_ignoring_the_notice_is_403_for_everyone_else()
    {
        var shop = await ShopAsync();
        var otherOwner = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertBusinessAsync(otherOwner);

        foreach (var (caller, query) in new[]
        {
            (shop.CustomerId, ""),
            (shop.CustomerId, $"&staffId={shop.StaffA}"),
            (shop.UserA, ""),                               // staff must name their own staffId
            (shop.UserA, $"&staffId={shop.StaffB}"),        // a colleague's slots
            (shop.RemovedUser, $"&staffId={shop.RemovedStaff}"),
            (otherOwner, ""),
            (await fixture.Sql.InsertAdminUserAsync(), ""),
        })
        {
            await Json.ErrorAsync(await GetDay(caller, shop, query + "&ignoreMinimumNotice=true"), HttpStatusCode.Forbidden, "Forbidden");
        }
    }

    [Fact]
    public async Task R3_ignoreMinimumNotice_false_is_allowed_for_anyone()
    {
        var shop = await ShopAsync();

        Assert.Equal(10, Slots(await OkAsync(GetDay(shop.CustomerId, shop, "&ignoreMinimumNotice=false"))).Count);
    }
}
