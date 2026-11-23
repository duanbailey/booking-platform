using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T11: weekly availability and date exceptions (the R6 check, T19, is in AvailabilityR6Tests). R8: an exception
/// replaces the weekly availability for its date, either different hours or none; blocks and
/// ranges on one day cannot overlap, and a date has either ranges or one "unavailable" row.
/// Staff story "weekly recurring availability plus date exceptions" (first three scenarios).
/// Access (decided 2026-11-19): the owner and the business's active staff may view; staff set
/// their own, the owner any (also a removed member's); everyone else gets 404, a colleague 403
/// for setting. LocalTime is HH:mm, so availability ends at 23:59 at the latest.
/// </summary>
public class AvailabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static string WeeklyPathFor(Guid staffId) => $"/v1/staff/{staffId}/weekly-availability";
    private static string ExceptionsPathFor(Guid staffId) => $"/v1/staff/{staffId}/date-exceptions";
    private static string ExceptionPathFor(Guid staffId, string date) => $"/v1/staff/{staffId}/date-exceptions/{date}";

    private const string Week =
        """{"blocks":[{"dayOfWeek":"Tuesday","startLocal":"13:00","endLocal":"17:00"},{"dayOfWeek":"Sunday","startLocal":"10:00","endLocal":"12:00"},{"dayOfWeek":"Tuesday","startLocal":"09:00","endLocal":"12:00"},{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"17:00"}]}""";

    /// <summary>A business with its owner, a staff member (the subject), a colleague, a removed member, and outsiders.</summary>
    private sealed record Cast(
        Guid OwnerId, Guid BusinessId, Guid MemberUserId, Guid StaffId, Guid ColleagueUserId,
        Guid RemovedUserId, Guid RemovedStaffId, Guid StrangerId, Guid AdminId, Guid OtherOwnerId);

    private async Task<Cast> CastAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync();
        var businessId = await fixture.Sql.InsertBusinessAsync(owner);
        var member = await fixture.Sql.InsertUserAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId, member);
        var colleague = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertStaffAsync(businessId, colleague);
        var removedUser = await fixture.Sql.InsertUserAsync();
        var removedStaff = await fixture.Sql.InsertStaffAsync(businessId, removedUser);
        await fixture.Sql.RemoveStaffAsync(removedStaff);
        var otherOwner = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertBusinessAsync(otherOwner);
        return new Cast(owner, businessId, member, staffId, colleague, removedUser, removedStaff,
            await fixture.Sql.InsertUserAsync(), await fixture.Sql.InsertAdminUserAsync(), otherOwner);
    }

    private Task<HttpResponseMessage> GetWeekly(Guid callerId, Guid staffId) =>
        fixture.Api.ClientFor(callerId).GetAsync(WeeklyPathFor(staffId));

    private Task<HttpResponseMessage> PutWeekly(Guid callerId, Guid staffId, string json) =>
        fixture.Api.ClientFor(callerId).PutAsync(WeeklyPathFor(staffId), Json.Body(json));

    private Task<HttpResponseMessage> ListExceptions(Guid callerId, Guid staffId, string query = "") =>
        fixture.Api.ClientFor(callerId).GetAsync(ExceptionsPathFor(staffId) + query);

    private Task<HttpResponseMessage> PutException(Guid callerId, Guid staffId, string date, string json) =>
        fixture.Api.ClientFor(callerId).PutAsync(ExceptionPathFor(staffId, date), Json.Body(json));

    private Task<HttpResponseMessage> DeleteException(Guid callerId, Guid staffId, string date, string query = "") =>
        fixture.Api.ClientFor(callerId).DeleteAsync(ExceptionPathFor(staffId, date) + query);

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    /// <summary>The staff member's weekly rows as "Day HH:mm-HH:mm", sorted, read straight from the database.</summary>
    private async Task<string> StoredWeekAsync(Guid staffId) => Convert.ToString(await fixture.Sql.ScalarAsync(
        "SELECT IFNULL(GROUP_CONCAT(CONCAT(day_of_week, ' ', TIME_FORMAT(start_local, '%H:%i'), '-', TIME_FORMAT(end_local, '%H:%i')) " +
        "ORDER BY day_of_week, start_local SEPARATOR ', '), '') FROM availability_rules WHERE staff_id = @staffId", new { staffId }))!;

    /// <summary>The rows for one date as "HH:mm-HH:mm" or "unavailable", read straight from the database.</summary>
    private async Task<string> StoredDateAsync(Guid staffId, string date) => Convert.ToString(await fixture.Sql.ScalarAsync(
        "SELECT IFNULL(GROUP_CONCAT(IF(start_local IS NULL, 'unavailable', CONCAT(TIME_FORMAT(start_local, '%H:%i'), '-', TIME_FORMAT(end_local, '%H:%i'))) " +
        "ORDER BY start_local SEPARATOR ', '), '') FROM availability_exceptions WHERE staff_id = @staffId AND date = @date", new { staffId, date }))!;

    private static string Blocks(JsonElement body) => string.Join(", ", body.GetProperty("blocks").EnumerateArray().Select(b =>
        $"{b.GetProperty("dayOfWeek").GetString()} {b.GetProperty("startLocal").GetString()}-{b.GetProperty("endLocal").GetString()}"));

    private static string Ranges(JsonElement exception) => string.Join(", ", exception.GetProperty("ranges").EnumerateArray().Select(r =>
        $"{r.GetProperty("startLocal").GetString()}-{r.GetProperty("endLocal").GetString()}"));

    // ------------------------------------------------------------ Weekly availability

    [Fact]
    public async Task New_staff_member_has_no_weekly_availability()
    {
        var cast = await CastAsync();

        var body = await Json.ReadAsync(await GetWeekly(cast.MemberUserId, cast.StaffId), HttpStatusCode.OK);

        Assert.Empty(body.GetProperty("blocks").EnumerateArray());
    }

    [Fact]
    public async Task Staff_member_sets_their_weekly_availability_ordered_monday_first()
    {
        // Story scenario 1: the availability is saved.
        var cast = await CastAsync();

        var body = await Json.ReadAsync(await PutWeekly(cast.MemberUserId, cast.StaffId, Week), HttpStatusCode.OK);

        const string expected = "Monday 09:00-17:00, Tuesday 09:00-12:00, Tuesday 13:00-17:00, Sunday 10:00-12:00";
        Assert.Equal(expected, Blocks(body));
        Assert.Single(body.EnumerateObject());
        Assert.Equal(expected, Blocks(await Json.ReadAsync(await GetWeekly(cast.OwnerId, cast.StaffId), HttpStatusCode.OK)));
        Assert.Equal(4, await CountAsync("SELECT COUNT(*) FROM availability_rules WHERE staff_id = @staffId", new { cast.StaffId }));
    }

    [Fact]
    public async Task Put_replaces_the_whole_week_and_an_empty_list_clears_it()
    {
        var cast = await CastAsync();
        await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.StaffId, Week), HttpStatusCode.OK);

        await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.StaffId,
            """{"blocks":[{"dayOfWeek":"Friday","startLocal":"08:30","endLocal":"23:59"}]}"""), HttpStatusCode.OK);
        Assert.Equal("Friday 08:30-23:59", await StoredWeekAsync(cast.StaffId));

        var cleared = await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.StaffId, """{"blocks":[]}"""), HttpStatusCode.OK);
        Assert.Empty(cleared.GetProperty("blocks").EnumerateArray());
        Assert.Equal("", await StoredWeekAsync(cast.StaffId));
    }

    [Fact]
    public async Task Adjacent_blocks_and_the_same_hours_on_different_days_are_allowed()
    {
        // Half-open ranges: 09:00-12:00 and 12:00-17:00 don't overlap (R1's convention).
        var cast = await CastAsync();

        await Json.ReadAsync(await PutWeekly(cast.MemberUserId, cast.StaffId,
            """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"12:00"},{"dayOfWeek":"Monday","startLocal":"12:00","endLocal":"17:00"},{"dayOfWeek":"Tuesday","startLocal":"09:00","endLocal":"12:00"}]}"""),
            HttpStatusCode.OK);

        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM availability_rules WHERE staff_id = @staffId", new { cast.StaffId }));
    }

    [Fact]
    public async Task A_confirmation_with_nothing_affected_goes_ahead()
    {
        // The confirm pattern: when nothing needs confirming, the change is made whether or not confirm was sent (R6 tests: AvailabilityR6Tests).
        var cast = await CastAsync();

        await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.StaffId,
            $$$"""{"blocks":[],"confirm":{"affectedBookingIds":["{{{Guid.NewGuid()}}}"]}}"""), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"12:00"},{"dayOfWeek":"Monday","startLocal":"11:59","endLocal":"13:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"17:00"},{"dayOfWeek":"Monday","startLocal":"10:00","endLocal":"11:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"09:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"17:00","endLocal":"09:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"9:00","endLocal":"17:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"24:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"12:60"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00:00","endLocal":"12:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Funday","startLocal":"09:00","endLocal":"12:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":1,"startLocal":"09:00","endLocal":"12:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"startLocal":"09:00","endLocal":"12:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","endLocal":"12:00"}]}""")]
    [InlineData("blocks", """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"12:00","note":"x"}]}""")]
    [InlineData("blocks", """{"blocks":[null]}""")]
    [InlineData("blocks", "{}")]
    [InlineData("blocks", """{"blocks":null}""")]
    [InlineData("confirm", """{"blocks":[],"confirm":{"affectedBookingIds":[]}}""")]
    public async Task Invalid_week_returns_400_and_nothing_changes(string field, string json)
    {
        var cast = await CastAsync();
        await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.StaffId, Week), HttpStatusCode.OK);
        var before = await StoredWeekAsync(cast.StaffId);

        var error = await Json.ErrorAsync(await PutWeekly(cast.OwnerId, cast.StaffId, json), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(Json.FieldNames(error), f => f.StartsWith(field, StringComparison.Ordinal));
        Assert.Equal(before, await StoredWeekAsync(cast.StaffId));
    }

    [Fact]
    public async Task Weekly_put_waits_for_the_staff_row_lock_and_then_replaces_everything()
    {
        var cast = await CastAsync();
        Task<HttpResponseMessage> put;
        await using (var held = await fixture.Sql.LockRowAsync("staff", cast.StaffId))
        {
            put = PutWeekly(cast.OwnerId, cast.StaffId, """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"10:00"}]}""");
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync(
                "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staffId, 'Friday', '09:00', '10:00')",
                new { id = Guid.NewGuid(), cast.StaffId });
            await held.CommitAsync();
        }

        await Json.ReadAsync(await put, HttpStatusCode.OK);

        Assert.Equal("Monday 09:00-10:00", await StoredWeekAsync(cast.StaffId));
    }

    [Fact]
    public async Task Who_may_view_weekly_availability()
    {
        var cast = await CastAsync();

        foreach (var caller in new[] { cast.OwnerId, cast.MemberUserId, cast.ColleagueUserId })
        {
            Assert.Equal(HttpStatusCode.OK, (await GetWeekly(caller, cast.StaffId)).StatusCode);
        }
        foreach (var caller in new[] { cast.RemovedUserId, cast.StrangerId, cast.AdminId, cast.OtherOwnerId })
        {
            await Json.ErrorAsync(await GetWeekly(caller, cast.StaffId), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await GetWeekly(cast.OwnerId, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync(WeeklyPathFor(cast.StaffId)), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Who_may_set_weekly_availability()
    {
        var cast = await CastAsync();
        const string monday = """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"10:00"}]}""";

        await Json.ErrorAsync(await PutWeekly(cast.ColleagueUserId, cast.StaffId, monday), HttpStatusCode.Forbidden, "Forbidden");
        foreach (var caller in new[] { cast.RemovedUserId, cast.StrangerId, cast.AdminId, cast.OtherOwnerId })
        {
            await Json.ErrorAsync(await PutWeekly(caller, cast.StaffId, monday), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await PutWeekly(cast.OwnerId, Guid.NewGuid(), monday), HttpStatusCode.NotFound, "NotFound");
        Assert.Equal("", await StoredWeekAsync(cast.StaffId));

        await Json.ReadAsync(await PutWeekly(cast.MemberUserId, cast.StaffId, monday), HttpStatusCode.OK);
        await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.StaffId, monday), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Owner_views_and_sets_a_removed_member_s_availability_but_the_member_cannot()
    {
        var cast = await CastAsync();
        const string monday = """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"10:00"}]}""";

        await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.RemovedStaffId, monday), HttpStatusCode.OK);
        await Json.ReadAsync(await GetWeekly(cast.OwnerId, cast.RemovedStaffId), HttpStatusCode.OK);
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.RemovedStaffId, "2026-12-01", """{"unavailable":true}"""), HttpStatusCode.OK);

        await Json.ErrorAsync(await GetWeekly(cast.RemovedUserId, cast.RemovedStaffId), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await PutWeekly(cast.RemovedUserId, cast.RemovedStaffId, monday), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await ListExceptions(cast.RemovedUserId, cast.RemovedStaffId), HttpStatusCode.NotFound, "NotFound");
    }

    // ------------------------------------------------------------ Date exceptions

    [Fact]
    public async Task Staff_member_sets_different_hours_for_a_date()
    {
        // Story scenario 2.
        var cast = await CastAsync();

        var body = await Json.ReadAsync(await PutException(cast.MemberUserId, cast.StaffId, "2026-12-24",
            """{"ranges":[{"startLocal":"14:00","endLocal":"16:00"},{"startLocal":"09:00","endLocal":"12:00"}]}"""), HttpStatusCode.OK);

        Assert.Equal("2026-12-24", body.GetProperty("date").GetString());
        Assert.False(body.GetProperty("unavailable").GetBoolean());
        Assert.Equal("09:00-12:00, 14:00-16:00", Ranges(body));
        Assert.Equal(3, body.EnumerateObject().Count());
        Assert.Equal("09:00-12:00, 14:00-16:00", await StoredDateAsync(cast.StaffId, "2026-12-24"));
    }

    [Fact]
    public async Task Staff_member_marks_a_date_unavailable()
    {
        // Story scenario 3: one row with both times null (specs/05-data-model.md).
        var cast = await CastAsync();

        var body = await Json.ReadAsync(await PutException(cast.MemberUserId, cast.StaffId, "2026-12-25", """{"unavailable":true}"""),
            HttpStatusCode.OK);

        Assert.True(body.GetProperty("unavailable").GetBoolean());
        Assert.Empty(body.GetProperty("ranges").EnumerateArray());
        Assert.Equal("unavailable", await StoredDateAsync(cast.StaffId, "2026-12-25"));
    }

    [Fact]
    public async Task Put_replaces_that_date_entirely_and_leaves_other_dates_alone()
    {
        var cast = await CastAsync();
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-24",
            """{"ranges":[{"startLocal":"09:00","endLocal":"12:00"},{"startLocal":"13:00","endLocal":"15:00"}]}"""), HttpStatusCode.OK);
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-26", """{"unavailable":true}"""), HttpStatusCode.OK);

        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-24", """{"unavailable":true}"""), HttpStatusCode.OK);
        Assert.Equal("unavailable", await StoredDateAsync(cast.StaffId, "2026-12-24"));

        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-24",
            """{"ranges":[{"startLocal":"10:00","endLocal":"11:00"}]}"""), HttpStatusCode.OK);
        Assert.Equal("10:00-11:00", await StoredDateAsync(cast.StaffId, "2026-12-24"));
        Assert.Equal("unavailable", await StoredDateAsync(cast.StaffId, "2026-12-26"));
    }

    [Fact]
    public async Task Exceptions_leave_the_weekly_availability_alone()
    {
        // R8: an exception replaces the weekly availability for its date only; the weekly rows stay.
        var cast = await CastAsync();
        await Json.ReadAsync(await PutWeekly(cast.OwnerId, cast.StaffId, Week), HttpStatusCode.OK);
        var week = await StoredWeekAsync(cast.StaffId);

        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-21", """{"unavailable":true}"""), HttpStatusCode.OK);

        Assert.Equal(week, await StoredWeekAsync(cast.StaffId));
    }

    [Fact]
    public async Task Past_date_may_have_an_exception()
    {
        var cast = await CastAsync();

        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2020-01-01", """{"unavailable":true}"""), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("ranges", """{"ranges":[{"startLocal":"09:00","endLocal":"12:00"},{"startLocal":"11:00","endLocal":"13:00"}]}""")]
    [InlineData("ranges", """{"ranges":[{"startLocal":"12:00","endLocal":"09:00"}]}""")]
    [InlineData("ranges", """{"ranges":[{"startLocal":"09:00","endLocal":"09:00"}]}""")]
    [InlineData("ranges", """{"ranges":[{"startLocal":"09:00","endLocal":"24:00"}]}""")]
    [InlineData("ranges", """{"ranges":[]}""")]
    [InlineData("ranges", """{"ranges":null}""")]
    [InlineData("ranges", """{"ranges":[{"startLocal":"09:00"}]}""")]
    [InlineData("unavailable", """{"unavailable":false}""")]
    [InlineData("unavailable", """{"unavailable":null}""")]
    [InlineData("unavailable", """{"unavailable":true,"ranges":[{"startLocal":"09:00","endLocal":"12:00"}]}""")]
    [InlineData("unavailable", """{"unavailable":false,"ranges":[{"startLocal":"09:00","endLocal":"12:00"}]}""")]
    [InlineData("", "{}")]
    [InlineData("", """{"unavailable":true,"colour":"red"}""")]
    [InlineData("confirm", """{"unavailable":true,"confirm":{}}""")]
    public async Task Invalid_exception_returns_400_and_nothing_changes(string field, string json)
    {
        var cast = await CastAsync();
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-24", """{"unavailable":true}"""), HttpStatusCode.OK);

        var error = await Json.ErrorAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-24", json),
            HttpStatusCode.BadRequest, "ValidationFailed");

        if (field != "") Assert.Contains(Json.FieldNames(error), f => f.StartsWith(field, StringComparison.Ordinal));
        Assert.Equal("unavailable", await StoredDateAsync(cast.StaffId, "2026-12-24"));
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("20261224")]
    [InlineData("2026-12-24T00:00")]
    [InlineData("tomorrow")]
    public async Task Malformed_date_in_the_path_is_400_naming_date(string date)
    {
        var cast = await CastAsync();

        var put = await Json.ErrorAsync(await PutException(cast.OwnerId, cast.StaffId, date, """{"unavailable":true}"""),
            HttpStatusCode.BadRequest, "ValidationFailed");
        var delete = await Json.ErrorAsync(await DeleteException(cast.OwnerId, cast.StaffId, date), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("date", Json.FieldNames(put));
        Assert.Contains("date", Json.FieldNames(delete));
    }

    [Fact]
    public async Task Delete_returns_the_date_to_the_weekly_availability()
    {
        var cast = await CastAsync();
        await Json.ReadAsync(await PutException(cast.MemberUserId, cast.StaffId, "2026-12-24",
            """{"ranges":[{"startLocal":"09:00","endLocal":"12:00"},{"startLocal":"13:00","endLocal":"15:00"}]}"""), HttpStatusCode.OK);
        await Json.ReadAsync(await PutException(cast.MemberUserId, cast.StaffId, "2026-12-26", """{"unavailable":true}"""), HttpStatusCode.OK);

        var response = await DeleteException(cast.MemberUserId, cast.StaffId, "2026-12-24");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await StoredDateAsync(cast.StaffId, "2026-12-24"));
        Assert.Equal("unavailable", await StoredDateAsync(cast.StaffId, "2026-12-26"));
    }

    [Fact]
    public async Task Deleting_a_date_without_an_exception_is_404()
    {
        var cast = await CastAsync();

        await Json.ErrorAsync(await DeleteException(cast.OwnerId, cast.StaffId, "2026-12-24"), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Delete_with_a_confirmation_and_nothing_affected_goes_ahead()
    {
        var cast = await CastAsync();
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-24", """{"unavailable":true}"""), HttpStatusCode.OK);

        var response = await DeleteException(cast.OwnerId, cast.StaffId, "2026-12-24",
            $"?confirmAffectedBookingIds={Guid.NewGuid()},{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Exception_put_waits_for_the_staff_row_lock_and_then_replaces_that_date()
    {
        var cast = await CastAsync();
        Task<HttpResponseMessage> put;
        await using (var held = await fixture.Sql.LockRowAsync("staff", cast.StaffId))
        {
            put = PutException(cast.OwnerId, cast.StaffId, "2026-12-24", """{"ranges":[{"startLocal":"09:00","endLocal":"10:00"}]}""");
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync(
                "INSERT INTO availability_exceptions (id, staff_id, date, start_local, end_local) VALUES (@id, @staffId, '2026-12-24', NULL, NULL)",
                new { id = Guid.NewGuid(), cast.StaffId });
            await held.CommitAsync();
        }

        await Json.ReadAsync(await put, HttpStatusCode.OK);

        Assert.Equal("09:00-10:00", await StoredDateAsync(cast.StaffId, "2026-12-24"));
    }

    [Fact]
    public async Task Who_may_set_and_delete_exceptions()
    {
        var cast = await CastAsync();
        const string date = "2026-12-24";
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, date, """{"unavailable":true}"""), HttpStatusCode.OK);
        const string hours = """{"ranges":[{"startLocal":"09:00","endLocal":"10:00"}]}""";

        await Json.ErrorAsync(await PutException(cast.ColleagueUserId, cast.StaffId, date, hours), HttpStatusCode.Forbidden, "Forbidden");
        await Json.ErrorAsync(await DeleteException(cast.ColleagueUserId, cast.StaffId, date), HttpStatusCode.Forbidden, "Forbidden");
        foreach (var caller in new[] { cast.RemovedUserId, cast.StrangerId, cast.AdminId, cast.OtherOwnerId })
        {
            await Json.ErrorAsync(await PutException(caller, cast.StaffId, date, hours), HttpStatusCode.NotFound, "NotFound");
            await Json.ErrorAsync(await DeleteException(caller, cast.StaffId, date), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await PutException(cast.OwnerId, Guid.NewGuid(), date, hours), HttpStatusCode.NotFound, "NotFound");
        Assert.Equal("unavailable", await StoredDateAsync(cast.StaffId, date));
    }

    [Fact]
    public async Task Callers_who_may_not_write_are_refused_without_waiting_for_the_staff_lock()
    {
        // A stranger (404) or colleague (403) never takes the staff lock, so they can't hold up
        // writes or bookings for that staff member; here the lock is held throughout.
        var cast = await CastAsync();
        const string monday = """{"blocks":[{"dayOfWeek":"Monday","startLocal":"09:00","endLocal":"10:00"}]}""";
        await using var held = await fixture.Sql.LockRowAsync("staff", cast.StaffId);

        var requests = new (Task<HttpResponseMessage> Request, HttpStatusCode Expected)[]
        {
            (PutWeekly(cast.StrangerId, cast.StaffId, monday), HttpStatusCode.NotFound),
            (PutException(cast.StrangerId, cast.StaffId, "2026-12-24", """{"unavailable":true}"""), HttpStatusCode.NotFound),
            (DeleteException(cast.StrangerId, cast.StaffId, "2026-12-24"), HttpStatusCode.NotFound),
            (PutWeekly(cast.ColleagueUserId, cast.StaffId, monday), HttpStatusCode.Forbidden),
            (DeleteException(cast.ColleagueUserId, cast.StaffId, "2026-12-24"), HttpStatusCode.Forbidden),
        };
        var all = Task.WhenAll(requests.Select(r => r.Request));

        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5))));
        foreach (var (request, expected) in requests) Assert.Equal(expected, (await request).StatusCode);
    }

    // ------------------------------------------------------------ Listing exceptions

    private static List<string> Dates(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("date").GetString()!).ToList();

    private async Task<Cast> CastWithExceptionsAsync()
    {
        var cast = await CastAsync();
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-26", """{"unavailable":true}"""), HttpStatusCode.OK);
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-12-24",
            """{"ranges":[{"startLocal":"13:00","endLocal":"15:00"},{"startLocal":"09:00","endLocal":"12:00"}]}"""), HttpStatusCode.OK);
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.StaffId, "2026-11-01", """{"unavailable":true}"""), HttpStatusCode.OK);
        await Json.ReadAsync(await PutException(cast.OwnerId, cast.RemovedStaffId, "2026-12-25", """{"unavailable":true}"""),
            HttpStatusCode.OK); // another staff member's exception: not listed
        return cast;
    }

    [Fact]
    public async Task Exceptions_are_listed_by_date_one_item_per_date()
    {
        var cast = await CastWithExceptionsAsync();

        var page = await Json.ReadAsync(await ListExceptions(cast.ColleagueUserId, cast.StaffId), HttpStatusCode.OK);

        Assert.Equal(["2026-11-01", "2026-12-24", "2026-12-26"], Dates(page));
        var christmasEve = page.GetProperty("items")[1];
        Assert.False(christmasEve.GetProperty("unavailable").GetBoolean());
        Assert.Equal("09:00-12:00, 13:00-15:00", Ranges(christmasEve));
        Assert.True(page.GetProperty("items")[2].GetProperty("unavailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("?from=2026-12-24", "2026-12-24,2026-12-26")]
    [InlineData("?to=2026-12-24", "2026-11-01,2026-12-24")]
    [InlineData("?from=2026-12-01&to=2026-12-25", "2026-12-24")]
    [InlineData("?from=2026-12-24&to=2026-12-24", "2026-12-24")]
    [InlineData("?from=2027-01-01", "")]
    public async Task From_and_to_are_inclusive_local_dates(string query, string expected)
    {
        var cast = await CastWithExceptionsAsync();

        var page = await Json.ReadAsync(await ListExceptions(cast.OwnerId, cast.StaffId, query), HttpStatusCode.OK);

        Assert.Equal(expected, string.Join(",", Dates(page)));
    }

    [Fact]
    public async Task Exception_pages_follow_the_date_cursor()
    {
        var cast = await CastWithExceptionsAsync();

        var first = await Json.ReadAsync(await ListExceptions(cast.OwnerId, cast.StaffId, "?limit=2&from=2026-11-01"), HttpStatusCode.OK);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await Json.ReadAsync(await ListExceptions(cast.OwnerId, cast.StaffId,
            "?limit=2&from=2026-11-01&cursor=" + Uri.EscapeDataString(cursor)), HttpStatusCode.OK);

        Assert.Equal(["2026-11-01", "2026-12-24"], Dates(first));
        Assert.Equal(["2026-12-26"], Dates(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("from", "?from=2026-12-25&to=2026-12-24")]
    [InlineData("from", "?from=2026-02-30")]
    [InlineData("to", "?to=next-week")]
    [InlineData("from", "?from=12/24/2026")]           // only yyyy-MM-dd, like the path date
    [InlineData("from", "?from=2026-12-24T10:00")]
    [InlineData("to", "?to=Dec%2024%202026")]
    [InlineData("to", "?to=2026-1-5")]
    [InlineData("from", "?from=")]
    [InlineData("limit", "?limit=0")]
    [InlineData("cursor", "?cursor=nope")]
    public async Task Bad_list_query_is_400_naming_the_parameter(string field, string query)
    {
        var cast = await CastAsync();

        var error = await Json.ErrorAsync(await ListExceptions(cast.OwnerId, cast.StaffId, query), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Fact]
    public async Task Cursor_from_another_kind_of_list_is_refused()
    {
        // A services cursor (name, id) is not a date cursor.
        var cast = await CastAsync();
        await fixture.Sql.InsertServiceAsync(cast.BusinessId, "A");
        await fixture.Sql.InsertServiceAsync(cast.BusinessId, "B");
        var services = await Json.ReadAsync(await fixture.Api.ClientFor(cast.OwnerId)
            .GetAsync($"/v1/businesses/{cast.BusinessId}/services?limit=1"), HttpStatusCode.OK);
        var servicesCursor = services.GetProperty("nextCursor").GetString()!;

        var error = await Json.ErrorAsync(await ListExceptions(cast.OwnerId, cast.StaffId, "?cursor=" + Uri.EscapeDataString(servicesCursor)),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("cursor", Json.FieldNames(error));
    }

    [Fact]
    public async Task Who_may_list_exceptions()
    {
        var cast = await CastAsync();

        foreach (var caller in new[] { cast.OwnerId, cast.MemberUserId, cast.ColleagueUserId })
        {
            Assert.Equal(HttpStatusCode.OK, (await ListExceptions(caller, cast.StaffId)).StatusCode);
        }
        foreach (var caller in new[] { cast.RemovedUserId, cast.StrangerId, cast.AdminId, cast.OtherOwnerId })
        {
            await Json.ErrorAsync(await ListExceptions(caller, cast.StaffId), HttpStatusCode.NotFound, "NotFound");
        }
    }
}
