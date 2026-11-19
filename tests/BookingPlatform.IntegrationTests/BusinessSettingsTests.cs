using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T8: GET /v1/businesses/{businessId} and PATCH .../settings. Story "Owner sets up their
/// business": only the fields sent change and the others keep their values (defaults from
/// specs/05-data-model.md); changes never rewrite existing bookings (R3, R4, R8). Any signed-in
/// user may view a business; only its owner may change it (403 for anyone else, 404 for a
/// business that does not exist; specs/07-nfr.md). Maxima decided 2026-11-19.
/// </summary>
public class BusinessSettingsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static string PathFor(Guid businessId) => $"/v1/businesses/{businessId}";
    private static string SettingsPathFor(Guid businessId) => $"/v1/businesses/{businessId}/settings";

    private Task<HttpResponseMessage> Get(Guid callerId, Guid businessId) =>
        fixture.Api.ClientFor(callerId).GetAsync(PathFor(businessId));

    private Task<HttpResponseMessage> Patch(Guid callerId, Guid businessId, string json) =>
        fixture.Api.ClientFor(callerId).PatchAsync(SettingsPathFor(businessId), Json.Body(json));

    private async Task<(Guid OwnerId, Guid BusinessId)> BusinessAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync();
        return (owner, await fixture.Sql.InsertBusinessAsync(owner));
    }

    /// <summary>The business row as the Business schema, read straight from the database.</summary>
    private async Task<string> StoredAsync(Guid businessId) => Convert.ToString(await fixture.Sql.ScalarAsync(
        "SELECT CONCAT_WS('|', name, time_zone, min_notice_minutes, cancellation_window_hours, max_advance_days, slot_step_minutes) " +
        "FROM businesses WHERE id = @businessId", new { businessId }))!;

    private static void AssertBusiness(JsonElement body, Guid id, string name, int minNotice, int cancellationWindow,
        int maxAdvance, int slotStep, string timeZone = "Europe/London")
    {
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.Equal(timeZone, body.GetProperty("timeZone").GetString());
        Assert.Equal(minNotice, body.GetProperty("minNoticeMinutes").GetInt32());
        Assert.Equal(cancellationWindow, body.GetProperty("cancellationWindowHours").GetInt32());
        Assert.Equal(maxAdvance, body.GetProperty("maxAdvanceDays").GetInt32());
        Assert.Equal(slotStep, body.GetProperty("slotStepMinutes").GetInt32());
        Assert.Equal(7, body.EnumerateObject().Count()); // exactly the Business schema's properties
    }

    // ------------------------------------------------------------ GET

    [Fact]
    public async Task Any_signed_in_user_sees_the_business_with_its_default_settings()
    {
        var (_, businessId) = await BusinessAsync();
        var stranger = await fixture.Sql.InsertUserAsync(); // no role anywhere

        var body = await Json.ReadAsync(await Get(stranger, businessId), HttpStatusCode.OK);

        AssertBusiness(body, businessId, "Test Business", minNotice: 120, cancellationWindow: 24, maxAdvance: 21, slotStep: 30);
    }

    [Fact]
    public async Task Owner_and_staff_see_the_same_business()
    {
        var (owner, businessId) = await BusinessAsync();
        var staffUser = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertStaffAsync(businessId, staffUser);

        var asOwner = await Json.ReadAsync(await Get(owner, businessId), HttpStatusCode.OK);
        var asStaff = await Json.ReadAsync(await Get(staffUser, businessId), HttpStatusCode.OK);

        Assert.Equal(asOwner.GetRawText(), asStaff.GetRawText());
    }

    [Fact]
    public async Task Unknown_business_is_404()
    {
        var caller = await fixture.Sql.InsertUserAsync();

        await Json.ErrorAsync(await Get(caller, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Without_a_token_it_is_401()
    {
        var (_, businessId) = await BusinessAsync();

        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync(PathFor(businessId)),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    // ------------------------------------------------------------ PATCH: changes

    [Fact]
    public async Task Owner_changes_one_setting_and_the_others_keep_their_defaults()
    {
        var (owner, businessId) = await BusinessAsync();

        var body = await Json.ReadAsync(await Patch(owner, businessId, """{"minNoticeMinutes":60}"""), HttpStatusCode.OK);

        AssertBusiness(body, businessId, "Test Business", minNotice: 60, cancellationWindow: 24, maxAdvance: 21, slotStep: 30);
        Assert.Equal("Test Business|Europe/London|60|24|21|30", await StoredAsync(businessId));
    }

    [Fact]
    public async Task Owner_changes_the_name_and_every_setting_at_once()
    {
        var (owner, businessId) = await BusinessAsync();

        var body = await Json.ReadAsync(await Patch(owner, businessId,
            """{"name":"  Cut & Dry  ","minNoticeMinutes":0,"cancellationWindowHours":48,"maxAdvanceDays":90,"slotStepMinutes":15}"""),
            HttpStatusCode.OK);

        AssertBusiness(body, businessId, "Cut & Dry", minNotice: 0, cancellationWindow: 48, maxAdvance: 90, slotStep: 15);
        Assert.Equal("Cut & Dry|Europe/London|0|48|90|15", await StoredAsync(businessId)); // name stored trimmed
    }

    [Fact]
    public async Task Later_change_keeps_values_set_by_an_earlier_one()
    {
        var (owner, businessId) = await BusinessAsync();
        await Json.ReadAsync(await Patch(owner, businessId, """{"slotStepMinutes":15}"""), HttpStatusCode.OK);

        await Json.ReadAsync(await Patch(owner, businessId, """{"cancellationWindowHours":0}"""), HttpStatusCode.OK);

        Assert.Equal("Test Business|Europe/London|120|0|21|15", await StoredAsync(businessId));
    }

    [Fact]
    public async Task Sending_the_current_values_is_a_200_that_changes_nothing()
    {
        var (owner, businessId) = await BusinessAsync();

        await Json.ReadAsync(await Patch(owner, businessId, """{"name":"Test Business","slotStepMinutes":30}"""), HttpStatusCode.OK);

        Assert.Equal("Test Business|Europe/London|120|24|21|30", await StoredAsync(businessId));
    }

    [Fact]
    public async Task Minimum_notice_longer_than_the_advance_window_is_allowed()
    {
        // Settings are validated independently (decided 2026-11-19), e.g. to pause online booking.
        var (owner, businessId) = await BusinessAsync();

        await Json.ReadAsync(await Patch(owner, businessId, """{"minNoticeMinutes":525600,"maxAdvanceDays":1}"""), HttpStatusCode.OK);

        Assert.Equal("Test Business|Europe/London|525600|24|1|30", await StoredAsync(businessId));
    }

    [Fact]
    public async Task Changes_never_rewrite_existing_bookings() // R3, R4, R8
    {
        var booking = await fixture.Sql.InsertBookingFixtureAsync();
        var owner = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT owner_user_id FROM businesses WHERE id = @id", new { id = booking.BusinessId }))!);
        var bookingId = await fixture.Sql.InsertBookingAsync(booking);
        const string bookingRow =
            "SELECT CONCAT_WS('|', start_utc, end_utc, blocked_start_utc, blocked_end_utc, status, version) FROM bookings WHERE id = @bookingId";
        var before = await fixture.Sql.ScalarAsync(bookingRow, new { bookingId });

        await Json.ReadAsync(await Patch(owner, booking.BusinessId,
            """{"minNoticeMinutes":0,"cancellationWindowHours":1,"maxAdvanceDays":1,"slotStepMinutes":7}"""), HttpStatusCode.OK);

        Assert.Equal(before, await fixture.Sql.ScalarAsync(bookingRow, new { bookingId }));
    }

    [Fact]
    public async Task Two_simultaneous_changes_to_different_fields_both_stick()
    {
        // Only the sent columns are written, so neither change overwrites the other.
        var (owner, businessId) = await BusinessAsync();

        var responses = await Task.WhenAll(
            Patch(owner, businessId, """{"name":"Renamed"}"""),
            Patch(owner, businessId, """{"slotStepMinutes":20}"""),
            Patch(owner, businessId, """{"maxAdvanceDays":60}"""));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal("Renamed|Europe/London|120|24|60|20", await StoredAsync(businessId));
    }

    [Fact]
    public async Task Response_shows_a_field_another_change_committed_meanwhile()
    {
        // The response is the stored business after the change, not this request's first read:
        // here another change commits maxAdvanceDays while this one waits to write.
        var (owner, businessId) = await BusinessAsync();
        Task<HttpResponseMessage> patch;
        await using (var held = await fixture.Sql.LockRowAsync("businesses", businessId))
        {
            patch = Patch(owner, businessId, """{"slotStepMinutes":15}""");
            await fixture.Sql.WaitForLockWaitAsync("businesses");
            await held.ExecuteAsync("UPDATE businesses SET max_advance_days = 60 WHERE id = @businessId", new { businessId });
            await held.CommitAsync();
        }

        var body = await Json.ReadAsync(await patch, HttpStatusCode.OK);

        AssertBusiness(body, businessId, "Test Business", minNotice: 120, cancellationWindow: 24, maxAdvance: 60, slotStep: 15);
    }

    [Fact]
    public async Task Settings_change_emits_no_event()
    {
        var (owner, businessId) = await BusinessAsync();
        var before = Convert.ToInt64(await fixture.Sql.ScalarAsync("SELECT COUNT(*) FROM outbox"));

        await Json.ReadAsync(await Patch(owner, businessId, """{"slotStepMinutes":15}"""), HttpStatusCode.OK);

        Assert.Equal(before, Convert.ToInt64(await fixture.Sql.ScalarAsync("SELECT COUNT(*) FROM outbox")));
    }

    // ------------------------------------------------------------ PATCH: who may

    private async Task AssertForbiddenAndUnchanged(Guid callerId, Guid businessId)
    {
        var before = await StoredAsync(businessId);

        await Json.ErrorAsync(await Patch(callerId, businessId, """{"slotStepMinutes":15}"""), HttpStatusCode.Forbidden, "Forbidden");

        Assert.Equal(before, await StoredAsync(businessId));
    }

    [Fact]
    public async Task Staff_member_gets_403_and_nothing_changes()
    {
        var (_, businessId) = await BusinessAsync();
        var staffUser = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertStaffAsync(businessId, staffUser);

        await AssertForbiddenAndUnchanged(staffUser, businessId);
    }

    [Fact]
    public async Task Removed_staff_member_gets_403()
    {
        var (_, businessId) = await BusinessAsync();
        var staffUser = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.RemoveStaffAsync(await fixture.Sql.InsertStaffAsync(businessId, staffUser));

        await AssertForbiddenAndUnchanged(staffUser, businessId);
    }

    [Fact]
    public async Task Signed_in_user_without_a_role_gets_403()
    {
        // Anyone signed in can view the business, so the answer is 403, not 404 (specs/07-nfr.md).
        var (_, businessId) = await BusinessAsync();

        await AssertForbiddenAndUnchanged(await fixture.Sql.InsertUserAsync(), businessId);
    }

    [Fact]
    public async Task Owner_of_another_business_gets_403()
    {
        var (_, businessId) = await BusinessAsync();
        var (otherOwner, _) = await BusinessAsync();

        await AssertForbiddenAndUnchanged(otherOwner, businessId);
    }

    [Fact]
    public async Task Admin_who_is_not_the_owner_gets_403()
    {
        // Admin grants nothing inside a business (specs/07-nfr.md).
        var (_, businessId) = await BusinessAsync();

        await AssertForbiddenAndUnchanged(await fixture.Sql.InsertAdminUserAsync(), businessId);
    }

    [Fact]
    public async Task Unknown_business_is_404_even_for_an_owner()
    {
        var (owner, _) = await BusinessAsync();

        await Json.ErrorAsync(await Patch(owner, Guid.NewGuid(), """{"slotStepMinutes":15}"""), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Patch_without_a_token_is_401()
    {
        var (_, businessId) = await BusinessAsync();

        await Json.ErrorAsync(await fixture.Api.CreateClient().PatchAsync(SettingsPathFor(businessId), Json.Body("""{"slotStepMinutes":15}""")),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    // ------------------------------------------------------------ PATCH: validation

    [Theory]
    [InlineData("minNoticeMinutes", 0)]
    [InlineData("minNoticeMinutes", 525600)]
    [InlineData("cancellationWindowHours", 0)]
    [InlineData("cancellationWindowHours", 8760)]
    [InlineData("maxAdvanceDays", 1)]
    [InlineData("maxAdvanceDays", 3650)]
    [InlineData("slotStepMinutes", 1)]
    [InlineData("slotStepMinutes", 1440)]
    public async Task Each_setting_accepts_its_minimum_and_maximum(string field, int value)
    {
        var (owner, businessId) = await BusinessAsync();

        var body = await Json.ReadAsync(await Patch(owner, businessId, $$"""{"{{field}}":{{value}}}"""), HttpStatusCode.OK);

        Assert.Equal(value, body.GetProperty(field).GetInt32());
    }

    [Theory]
    [InlineData("minNoticeMinutes", "-1")]
    [InlineData("minNoticeMinutes", "525601")]
    [InlineData("cancellationWindowHours", "-1")]
    [InlineData("cancellationWindowHours", "8761")]
    [InlineData("maxAdvanceDays", "0")]
    [InlineData("maxAdvanceDays", "3651")]
    [InlineData("slotStepMinutes", "0")]
    [InlineData("slotStepMinutes", "1441")]
    [InlineData("slotStepMinutes", "null")]
    [InlineData("slotStepMinutes", "1.5")]
    [InlineData("slotStepMinutes", "\"30\"")]
    [InlineData("slotStepMinutes", "99999999999")]
    [InlineData("name", "null")]
    [InlineData("name", "\"   \"")]
    [InlineData("name", "\"\"")]
    public async Task Invalid_value_returns_400_ValidationFailed_naming_the_field_and_nothing_changes(string field, string value)
    {
        var (owner, businessId) = await BusinessAsync();
        var before = await StoredAsync(businessId);

        var error = await Json.ErrorAsync(await Patch(owner, businessId, $$"""{"{{field}}":{{value}}}"""),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
        Assert.Equal(before, await StoredAsync(businessId));
    }

    [Fact]
    public async Task Name_longer_than_200_characters_after_trimming_is_refused()
    {
        var (owner, businessId) = await BusinessAsync();

        await Json.ReadAsync(await Patch(owner, businessId, $$"""{"name":" {{new string('n', 200)}} "}"""), HttpStatusCode.OK);
        var error = await Json.ErrorAsync(await Patch(owner, businessId, $$"""{"name":"{{new string('n', 201)}}"}"""),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains("name", Json.FieldNames(error));
    }

    [Theory]
    [InlineData("minNoticeMinutes", "525601", "Must be between 0 and 525600.")]
    [InlineData("cancellationWindowHours", "-1", "Must be between 0 and 8760.")]
    [InlineData("maxAdvanceDays", "3651", "Must be between 1 and 3650.")]
    [InlineData("slotStepMinutes", "0", "Must be between 1 and 1440.")]
    public async Task Out_of_range_message_states_the_range_without_naming_code(string field, string value, string message)
    {
        var (owner, businessId) = await BusinessAsync();

        var error = await Json.ErrorAsync(await Patch(owner, businessId, $$"""{"{{field}}":{{value}}}"""),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Equal(message, error.GetProperty("details").GetProperty("fields").GetProperty(field).GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"timeZone":"America/New_York"}""")] // changed through time-zone-change instead
    [InlineData("""{"slotStepMinutes":15,"colour":"red"}""")]
    public async Task Empty_body_or_unknown_field_returns_400_and_nothing_changes(string json)
    {
        var (owner, businessId) = await BusinessAsync();
        var before = await StoredAsync(businessId);

        await Json.ErrorAsync(await Patch(owner, businessId, json), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Equal(before, await StoredAsync(businessId));
    }
}
