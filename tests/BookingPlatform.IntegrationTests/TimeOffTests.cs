using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T12: list, create and delete time off, without the R6 check (T18; `confirm` is accepted and
/// ignored until then). Decisions 2026-11-19: viewed like availability (the owner and the
/// business's active staff; the owner also for a removed member; everyone else 404); staff block
/// and remove their own, the owner anyone's (a colleague gets 403); entries may overlap and may
/// be in the past; timestamps are RFC 3339 with an uppercase Z only.
/// </summary>
public class TimeOffTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static string ListPathFor(Guid staffId) => $"/v1/staff/{staffId}/time-off";
    private static string ItemPathFor(Guid timeOffId) => $"/v1/time-off/{timeOffId}";

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

    private Task<HttpResponseMessage> List(Guid callerId, Guid staffId, string query = "") =>
        fixture.Api.ClientFor(callerId).GetAsync(ListPathFor(staffId) + query);

    private Task<HttpResponseMessage> Create(Guid callerId, Guid staffId, string startUtc, string endUtc) =>
        fixture.Api.ClientFor(callerId).PostJsonAsync(ListPathFor(staffId), new { startUtc, endUtc });

    private Task<HttpResponseMessage> CreateJson(Guid callerId, Guid staffId, string json) =>
        fixture.Api.ClientFor(callerId).PostAsync(ListPathFor(staffId), Json.Body(json));

    private Task<HttpResponseMessage> Delete(Guid callerId, Guid timeOffId) =>
        fixture.Api.ClientFor(callerId).DeleteAsync(ItemPathFor(timeOffId));

    private async Task<long> CountAsync(Guid staffId) => Convert.ToInt64(await fixture.Sql.ScalarAsync(
        "SELECT COUNT(*) FROM time_off WHERE staff_id = @staffId", new { staffId }));

    private async Task<Guid> CreatedIdAsync(Guid callerId, Guid staffId, string startUtc, string endUtc) =>
        (await Json.ReadAsync(await Create(callerId, staffId, startUtc, endUtc), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static List<string> Starts(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("startUtc").GetString()!).ToList();

    // ------------------------------------------------------------ Create

    [Fact]
    public async Task Staff_member_blocks_their_own_time_off()
    {
        var cast = await CastAsync();

        var body = await Json.ReadAsync(await Create(cast.MemberUserId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T17:30:00Z"),
            HttpStatusCode.Created);

        Assert.Equal(cast.StaffId, body.GetProperty("staffId").GetGuid());
        Assert.Equal("2026-12-24T09:00:00Z", body.GetProperty("startUtc").GetString());
        Assert.Equal("2026-12-24T17:30:00Z", body.GetProperty("endUtc").GetString());
        Assert.Equal(4, body.EnumerateObject().Count());
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("2026-12-24 09:00:00.000000|2026-12-24 17:30:00.000000", Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT CONCAT_WS('|', start_utc, end_utc) FROM time_off WHERE id = @id AND staff_id = @staffId", new { id, cast.StaffId })));
    }

    [Fact]
    public async Task Owner_blocks_time_off_for_any_staff_member_including_a_removed_one()
    {
        var cast = await CastAsync();

        await Json.ReadAsync(await Create(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z"), HttpStatusCode.Created);
        await Json.ReadAsync(await Create(cast.OwnerId, cast.RemovedStaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z"), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Time_off_may_overlap_other_time_off_and_lie_in_the_past()
    {
        var cast = await CastAsync();

        await Json.ReadAsync(await Create(cast.MemberUserId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T12:00:00Z"), HttpStatusCode.Created);
        await Json.ReadAsync(await Create(cast.MemberUserId, cast.StaffId, "2026-12-24T11:00:00Z", "2026-12-24T14:00:00Z"), HttpStatusCode.Created);
        await Json.ReadAsync(await Create(cast.MemberUserId, cast.StaffId, "2020-01-01T00:00:00Z", "2020-01-02T00:00:00Z"), HttpStatusCode.Created);

        Assert.Equal(3, await CountAsync(cast.StaffId));
    }

    [Fact]
    public async Task Fractional_seconds_are_kept_to_the_microsecond()
    {
        var cast = await CastAsync();

        var body = await Json.ReadAsync(await Create(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00.1234567Z", "2026-12-24T09:00:01.5Z"),
            HttpStatusCode.Created);

        Assert.Equal("2026-12-24T09:00:00.123456Z", body.GetProperty("startUtc").GetString());
        Assert.Equal("2026-12-24T09:00:01.5Z", body.GetProperty("endUtc").GetString());
        var listed = await Json.ReadAsync(await List(cast.OwnerId, cast.StaffId), HttpStatusCode.OK);
        Assert.Equal(["2026-12-24T09:00:00.123456Z"], Starts(listed));
    }

    [Fact]
    public async Task Confirm_is_accepted_and_ignored_until_the_R6_check_exists()
    {
        var cast = await CastAsync();

        await Json.ReadAsync(await CreateJson(cast.OwnerId, cast.StaffId,
            $$$"""{"startUtc":"2026-12-24T09:00:00Z","endUtc":"2026-12-24T10:00:00Z","confirm":{"affectedBookingIds":["{{{Guid.NewGuid()}}}"]}}"""),
            HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("endUtc", """{"startUtc":"2026-12-24T09:00:00Z","endUtc":"2026-12-24T09:00:00Z"}""")]
    [InlineData("endUtc", """{"startUtc":"2026-12-24T09:00:00Z","endUtc":"2026-12-24T08:00:00Z"}""")]
    [InlineData("startUtc", """{"startUtc":"2026-12-24T09:00:00+02:00","endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("startUtc", """{"startUtc":"2026-12-24T09:00:00","endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("startUtc", """{"startUtc":"2026-12-24T09:00:00z","endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("startUtc", """{"startUtc":"2026-12-24 09:00:00Z","endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("startUtc", """{"startUtc":"2026-12-24T09:00Z","endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("startUtc", """{"startUtc":"2026-02-30T09:00:00Z","endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("startUtc", """{"startUtc":"12/24/2026 09:00:00Z","endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("startUtc", """{"endUtc":"2026-12-24T10:00:00Z"}""")]
    [InlineData("endUtc", """{"startUtc":"2026-12-24T09:00:00Z","endUtc":null}""")]
    [InlineData("endUtc", """{"startUtc":"2026-12-24T09:00:00Z","endUtc":1766566800}""")]
    [InlineData("confirm", """{"startUtc":"2026-12-24T09:00:00Z","endUtc":"2026-12-24T10:00:00Z","confirm":{"affectedBookingIds":[]}}""")]
    [InlineData("staffId", """{"startUtc":"2026-12-24T09:00:00Z","endUtc":"2026-12-24T10:00:00Z","staffId":"00000000-0000-0000-0000-000000000001"}""")]
    public async Task Invalid_time_off_returns_400_naming_the_field_and_saves_nothing(string field, string json)
    {
        var cast = await CastAsync();

        var error = await Json.ErrorAsync(await CreateJson(cast.OwnerId, cast.StaffId, json), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(Json.FieldNames(error), f => f.StartsWith(field, StringComparison.Ordinal));
        Assert.Equal(0, await CountAsync(cast.StaffId));
    }

    [Fact]
    public async Task Who_may_block_time_off()
    {
        var cast = await CastAsync();

        await Json.ErrorAsync(await Create(cast.ColleagueUserId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z"),
            HttpStatusCode.Forbidden, "Forbidden");
        foreach (var caller in new[] { cast.RemovedUserId, cast.StrangerId, cast.AdminId, cast.OtherOwnerId })
        {
            await Json.ErrorAsync(await Create(caller, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z"),
                HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await Create(cast.RemovedUserId, cast.RemovedStaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z"),
            HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Create(cast.OwnerId, Guid.NewGuid(), "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z"),
            HttpStatusCode.NotFound, "NotFound");
        Assert.Equal(0, await CountAsync(cast.StaffId));
    }

    [Fact]
    public async Task Create_waits_for_the_staff_row_lock()
    {
        // The R6 check (T18) runs under the staff lock (ADR 0002); creating already takes it.
        var cast = await CastAsync();
        Task<HttpResponseMessage> create;
        await using (await fixture.Sql.LockRowAsync("staff", cast.StaffId))
        {
            create = Create(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z");
            await fixture.Sql.WaitForLockWaitAsync("staff");
            Assert.False(create.IsCompleted);
        }

        await Json.ReadAsync(await create, HttpStatusCode.Created);
    }

    [Fact]
    public async Task Callers_who_may_not_write_are_refused_without_waiting_for_the_staff_lock()
    {
        var cast = await CastAsync();
        var timeOffId = await CreatedIdAsync(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z");
        await using var held = await fixture.Sql.LockRowAsync("staff", cast.StaffId);

        var requests = new (Task<HttpResponseMessage> Request, HttpStatusCode Expected)[]
        {
            (Create(cast.StrangerId, cast.StaffId, "2026-12-25T09:00:00Z", "2026-12-25T10:00:00Z"), HttpStatusCode.NotFound),
            (Create(cast.ColleagueUserId, cast.StaffId, "2026-12-25T09:00:00Z", "2026-12-25T10:00:00Z"), HttpStatusCode.Forbidden),
            (Delete(cast.StrangerId, timeOffId), HttpStatusCode.NotFound),
            (Delete(cast.ColleagueUserId, timeOffId), HttpStatusCode.Forbidden),
        };
        var all = Task.WhenAll(requests.Select(r => r.Request));

        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5))));
        foreach (var (request, expected) in requests) Assert.Equal(expected, (await request).StatusCode);
    }

    // ------------------------------------------------------------ Delete

    [Fact]
    public async Task Staff_member_and_owner_remove_time_off()
    {
        var cast = await CastAsync();
        var own = await CreatedIdAsync(cast.MemberUserId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z");
        var byOwner = await CreatedIdAsync(cast.MemberUserId, cast.StaffId, "2026-12-25T09:00:00Z", "2026-12-25T10:00:00Z");
        var removedMembers = await CreatedIdAsync(cast.OwnerId, cast.RemovedStaffId, "2026-12-25T09:00:00Z", "2026-12-25T10:00:00Z");

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(cast.MemberUserId, own)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(cast.OwnerId, byOwner)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(cast.OwnerId, removedMembers)).StatusCode);
        Assert.Equal(0, await CountAsync(cast.StaffId));
        Assert.Equal(0, await CountAsync(cast.RemovedStaffId));
    }

    [Fact]
    public async Task Who_may_not_remove_time_off()
    {
        var cast = await CastAsync();
        var timeOffId = await CreatedIdAsync(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z");
        var removedMembers = await CreatedIdAsync(cast.OwnerId, cast.RemovedStaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z");

        await Json.ErrorAsync(await Delete(cast.ColleagueUserId, timeOffId), HttpStatusCode.Forbidden, "Forbidden");
        foreach (var caller in new[] { cast.RemovedUserId, cast.StrangerId, cast.AdminId, cast.OtherOwnerId })
        {
            await Json.ErrorAsync(await Delete(caller, timeOffId), HttpStatusCode.NotFound, "NotFound");
        }
        await Json.ErrorAsync(await Delete(cast.RemovedUserId, removedMembers), HttpStatusCode.NotFound, "NotFound");
        Assert.Equal(1, await CountAsync(cast.StaffId));
    }

    [Fact]
    public async Task Removing_unknown_or_already_removed_time_off_is_404()
    {
        var cast = await CastAsync();
        var timeOffId = await CreatedIdAsync(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z");
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(cast.OwnerId, timeOffId)).StatusCode);

        await Json.ErrorAsync(await Delete(cast.OwnerId, timeOffId), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await Delete(cast.OwnerId, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Removal_waiting_for_the_staff_lock_is_404_if_another_removal_won_meanwhile()
    {
        // Another removal holding the lock deletes the entry and commits; the waiting one must
        // then report 404, not 204, since it removed nothing.
        var cast = await CastAsync();
        var timeOffId = await CreatedIdAsync(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00Z", "2026-12-24T10:00:00Z");
        Task<HttpResponseMessage> delete;
        await using (var held = await fixture.Sql.LockRowAsync("staff", cast.StaffId))
        {
            delete = Delete(cast.OwnerId, timeOffId);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync("DELETE FROM time_off WHERE id = @timeOffId", new { timeOffId });
            await held.CommitAsync();
        }

        await Json.ErrorAsync(await delete, HttpStatusCode.NotFound, "NotFound");
    }

    // ------------------------------------------------------------ List

    private async Task<Cast> CastWithTimeOffAsync()
    {
        var cast = await CastAsync();
        foreach (var (start, end) in new[]
                 {
                     ("2026-12-26T09:00:00Z", "2026-12-26T10:00:00Z"), ("2026-12-24T09:00:00Z", "2026-12-24T17:00:00Z"),
                     ("2026-12-25T00:00:00Z", "2026-12-26T00:00:00Z"), ("2026-12-24T12:00:00Z", "2026-12-24T13:00:00Z"),
                 })
        {
            await CreatedIdAsync(cast.OwnerId, cast.StaffId, start, end);
        }
        await CreatedIdAsync(cast.OwnerId, cast.RemovedStaffId, "2026-12-24T10:00:00Z", "2026-12-24T11:00:00Z"); // not this member's
        return cast;
    }

    [Fact]
    public async Task Time_off_is_listed_by_start()
    {
        var cast = await CastWithTimeOffAsync();

        var page = await Json.ReadAsync(await List(cast.ColleagueUserId, cast.StaffId), HttpStatusCode.OK);

        Assert.Equal(["2026-12-24T09:00:00Z", "2026-12-24T12:00:00Z", "2026-12-25T00:00:00Z", "2026-12-26T09:00:00Z"], Starts(page));
        var first = page.GetProperty("items")[0];
        Assert.Equal("2026-12-24T17:00:00Z", first.GetProperty("endUtc").GetString());
        Assert.Equal(cast.StaffId, first.GetProperty("staffId").GetGuid());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    // Time off: 24th 09:00-17:00, 24th 12:00-13:00, 25th 00:00 to 26th 00:00, 26th 09:00-10:00.
    [Theory]
    [InlineData("?from=2026-12-24T12:00:00Z", "2026-12-24T09:00:00Z,2026-12-24T12:00:00Z,2026-12-25T00:00:00Z,2026-12-26T09:00:00Z")]
    [InlineData("?from=2026-12-24T17:00:00Z", "2026-12-25T00:00:00Z,2026-12-26T09:00:00Z")] // ending exactly at from: not under way
    [InlineData("?from=2026-12-25T06:00:00Z&to=2026-12-25T07:00:00Z", "2026-12-25T00:00:00Z")] // under way throughout the window
    [InlineData("?to=2026-12-25T00:00:00Z", "2026-12-24T09:00:00Z,2026-12-24T12:00:00Z")] // starting exactly at to: excluded
    [InlineData("?from=2026-12-26T00:00:00Z", "2026-12-26T09:00:00Z")]
    [InlineData("?from=2026-12-25T06:00:00Z&to=2026-12-25T06:00:00Z", "2026-12-25T00:00:00Z")] // an instant: what covers it
    [InlineData("?from=2026-12-27T00:00:00Z", "")]
    public async Task Filter_returns_time_off_overlapping_the_window(string query, string expected)
    {
        // Decided 2026-11-19: ends after from and starts before to, so time off already under way
        // at from is included (half-open, like every interval here).
        var cast = await CastWithTimeOffAsync();

        var page = await Json.ReadAsync(await List(cast.OwnerId, cast.StaffId, query), HttpStatusCode.OK);

        Assert.Equal(expected, string.Join(",", Starts(page)));
    }

    [Fact]
    public async Task Paging_with_an_overlap_filter_keeps_the_filter()
    {
        var cast = await CastWithTimeOffAsync();

        var first = await Json.ReadAsync(await List(cast.OwnerId, cast.StaffId, "?from=2026-12-24T12:00:00Z&limit=3"), HttpStatusCode.OK);
        var second = await Json.ReadAsync(await List(cast.OwnerId, cast.StaffId,
            "?from=2026-12-24T12:00:00Z&limit=3&cursor=" + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!)), HttpStatusCode.OK);

        Assert.Equal(["2026-12-24T09:00:00Z", "2026-12-24T12:00:00Z", "2026-12-25T00:00:00Z"], Starts(first));
        Assert.Equal(["2026-12-26T09:00:00Z"], Starts(second));
    }

    [Fact]
    public async Task Time_off_pages_follow_the_cursor_and_equal_starts_go_by_id()
    {
        var cast = await CastAsync();
        for (var i = 0; i < 3; i++) await CreatedIdAsync(cast.OwnerId, cast.StaffId, "2026-12-24T09:00:00Z", $"2026-12-24T1{i}:00:00Z");
        await CreatedIdAsync(cast.OwnerId, cast.StaffId, "2026-12-23T09:00:00Z", "2026-12-23T10:00:00Z");

        var seen = new List<JsonElement>();
        string? cursor = null;
        do
        {
            Assert.True(seen.Count < 10, "the cursor never reached the last page"); // a cursor bug fails, not hangs
            var page = await Json.ReadAsync(await List(cast.OwnerId, cast.StaffId,
                "?limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))), HttpStatusCode.OK);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.Clone()));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Select(e => e.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Equal("2026-12-23T09:00:00Z", seen[0].GetProperty("startUtc").GetString());
        var ties = seen.Skip(1).Select(e => e.GetProperty("id").GetGuid().ToString()).ToList();
        Assert.Equal(ties.Order(StringComparer.Ordinal), ties);
    }

    [Theory]
    [InlineData("from", "?from=2026-12-25T00:00:00Z&to=2026-12-24T00:00:00Z")]
    [InlineData("from", "?from=2026-12-24")]
    [InlineData("from", "?from=2026-12-24T09:00:00%2B02:00")]
    [InlineData("to", "?to=2026-12-24T09:00:00")]
    [InlineData("to", "?to=")]
    [InlineData("limit", "?limit=0")]
    [InlineData("cursor", "?cursor=nope")]
    public async Task Bad_list_query_is_400_naming_the_parameter(string field, string query)
    {
        var cast = await CastAsync();

        var error = await Json.ErrorAsync(await List(cast.OwnerId, cast.StaffId, query), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Fact]
    public async Task Cursor_from_another_kind_of_list_is_refused()
    {
        var cast = await CastAsync();
        await fixture.Sql.InsertServiceAsync(cast.BusinessId, "A");
        await fixture.Sql.InsertServiceAsync(cast.BusinessId, "B");
        var services = await Json.ReadAsync(await fixture.Api.ClientFor(cast.OwnerId)
            .GetAsync($"/v1/businesses/{cast.BusinessId}/services?limit=1"), HttpStatusCode.OK);

        var error = await Json.ErrorAsync(await List(cast.OwnerId, cast.StaffId,
            "?cursor=" + Uri.EscapeDataString(services.GetProperty("nextCursor").GetString()!)), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("cursor", Json.FieldNames(error));
    }

    [Fact]
    public async Task Who_may_view_time_off()
    {
        var cast = await CastAsync();

        foreach (var caller in new[] { cast.OwnerId, cast.MemberUserId, cast.ColleagueUserId })
        {
            Assert.Equal(HttpStatusCode.OK, (await List(caller, cast.StaffId)).StatusCode);
        }
        foreach (var caller in new[] { cast.RemovedUserId, cast.StrangerId, cast.AdminId, cast.OtherOwnerId })
        {
            await Json.ErrorAsync(await List(caller, cast.StaffId), HttpStatusCode.NotFound, "NotFound");
        }
        Assert.Equal(HttpStatusCode.OK, (await List(cast.OwnerId, cast.RemovedStaffId)).StatusCode);
        await Json.ErrorAsync(await List(cast.RemovedUserId, cast.RemovedStaffId), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await List(cast.OwnerId, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync(ListPathFor(cast.StaffId)), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Time_off_has_an_index_for_the_list_order()
    {
        // (staff_id, start_utc) from InitialSchema: InnoDB appends the primary key (id) to every
        // secondary index, so it already serves the (start, id) order and keyset.
        Assert.Equal("staff_id,start_utc", await fixture.Sql.ScalarAsync(
            "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM information_schema.STATISTICS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'time_off' " +
            "GROUP BY INDEX_NAME HAVING GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) = 'staff_id,start_utc'"));
    }
}
