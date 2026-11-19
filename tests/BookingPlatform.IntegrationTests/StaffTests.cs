using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T10: list and add staff, and PUT /v1/staff/{staffId}/services. Stories "Owner adds staff"
/// (link an account or create one with an invite; the owner may add themselves; AlreadyStaff;
/// re-adding a removed member restores the same row) and "Owner assigns staff to services".
/// Any signed-in user may list staff; only the owner may add them or set their services
/// (403 for anyone else, 404 if the business or staff member does not exist). Decisions
/// 2026-11-19: no AccessGranted when the owner adds themselves; restoring keeps service links
/// and availability; a removed member's services may be set; a new account gets the business's
/// time zone.
/// </summary>
public class StaffTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static string ListPathFor(Guid businessId) => $"/v1/businesses/{businessId}/staff";
    private static string ServicesPathFor(Guid staffId) => $"/v1/staff/{staffId}/services";

    private Task<HttpResponseMessage> List(Guid callerId, Guid businessId, string query = "") =>
        fixture.Api.ClientFor(callerId).GetAsync(ListPathFor(businessId) + query);

    private Task<HttpResponseMessage> Add(Guid callerId, Guid businessId, string email, string name = "  Sam Staff  ") =>
        fixture.Api.ClientFor(callerId).PostJsonAsync(ListPathFor(businessId), new { name, email });

    private Task<HttpResponseMessage> SetServices(Guid callerId, Guid staffId, params Guid[] serviceIds) =>
        fixture.Api.ClientFor(callerId).PutAsync(ServicesPathFor(staffId), Json.Body(JsonSerializer.Serialize(new { serviceIds })));

    private Task<HttpResponseMessage> SetServicesJson(Guid callerId, Guid staffId, string json) =>
        fixture.Api.ClientFor(callerId).PutAsync(ServicesPathFor(staffId), Json.Body(json));

    /// <summary>A business in America/New_York (not the users' Europe/London), so the new account's zone is telling.</summary>
    private async Task<(Guid OwnerId, Guid BusinessId)> BusinessAsync()
    {
        var owner = await fixture.Sql.InsertUserAsync();
        var businessId = await fixture.Sql.InsertBusinessAsync(owner);
        await fixture.Sql.ExecuteAsync("UPDATE businesses SET time_zone = 'America/New_York' WHERE id = @businessId", new { businessId });
        return (owner, businessId);
    }

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<long> StaffRowsAsync(Guid businessId) =>
        CountAsync("SELECT COUNT(*) FROM staff WHERE business_id = @businessId", new { businessId });

    private async Task<Guid?> UserIdAsync(string email) =>
        await fixture.Sql.ScalarAsync("SELECT id FROM users WHERE email = @email", new { email }) is { } id and not DBNull
            ? Guid.Parse(Convert.ToString(id)!) : null;

    /// <summary>The AccessGranted payloads written for a user, oldest first, each validated against its schema.</summary>
    private async Task<List<JsonElement>> AccessGrantedAsync(Guid userId)
    {
        var events = new List<JsonElement>();
        var count = await CountAsync(
            "SELECT COUNT(*) FROM outbox WHERE event_type = 'AccessGranted' AND message_group_id = @id", new { id = userId.ToString() });
        for (var i = 0; i < count; i++)
        {
            var payload = Convert.ToString(await fixture.Sql.ScalarAsync(
                "SELECT payload FROM outbox WHERE event_type = 'AccessGranted' AND message_group_id = @id " +
                "ORDER BY created_at, id LIMIT 1 OFFSET " + i, new { id = userId.ToString() }))!;
            EventSchemas.AssertValid(payload);
            events.Add(JsonDocument.Parse(payload).RootElement.GetProperty("data"));
        }
        return events;
    }

    private static void AssertStaff(JsonElement body, Guid businessId, Guid userId, string name)
    {
        Assert.Equal(businessId, body.GetProperty("businessId").GetGuid());
        Assert.Equal(userId, body.GetProperty("userId").GetGuid());
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("removedAt").ValueKind);
        Assert.Equal(5, body.EnumerateObject().Count()); // staffId, businessId, userId, name, removedAt
    }

    private static List<Guid> StaffIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("staffId").GetGuid()).ToList();

    // ------------------------------------------------------------ List

    [Fact]
    public async Task Any_signed_in_user_lists_active_staff_by_name()
    {
        var (_, businessId) = await BusinessAsync();
        var zoe = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "zoe"));
        var adam = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Adam"));
        var removed = await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Bea"));
        await fixture.Sql.RemoveStaffAsync(removed);
        var (_, otherBusiness) = await BusinessAsync();
        await fixture.Sql.InsertStaffAsync(otherBusiness, await fixture.Sql.InsertUserAsync(name: "Elsewhere"));

        var page = await Json.ReadAsync(await List(await fixture.Sql.InsertUserAsync(), businessId), HttpStatusCode.OK);

        Assert.Equal([adam, zoe], StaffIds(page));
        var first = page.GetProperty("items")[0];
        Assert.Equal("Adam", first.GetProperty("name").GetString());
        Assert.Equal(2, first.EnumerateObject().Count()); // StaffSummary
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Staff_pages_follow_the_cursor_and_same_names_go_by_staff_id()
    {
        var (owner, businessId) = await BusinessAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add(await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Sam")));
        ids.Add(await fixture.Sql.InsertStaffAsync(businessId, await fixture.Sql.InsertUserAsync(name: "Ann")));
        var samsById = new List<(Guid Id, string Text)>();
        foreach (var id in ids.Take(3)) samsById.Add((id, Convert.ToString(await fixture.Sql.ScalarAsync("SELECT id FROM staff WHERE id = @id", new { id }))!));
        var expected = new[] { ids[3] }.Concat(samsById.OrderBy(x => x.Text, StringComparer.Ordinal).Select(x => x.Id)).ToList();

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Json.ReadAsync(await List(owner, businessId,
                "?limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))), HttpStatusCode.OK);
            seen.AddRange(StaffIds(page));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal(expected, seen);
    }

    [Theory]
    [InlineData("limit", "?limit=0")]
    [InlineData("cursor", "?cursor=nope")]
    public async Task Bad_limit_or_cursor_returns_400(string field, string query)
    {
        var (owner, businessId) = await BusinessAsync();

        var error = await Json.ErrorAsync(await List(owner, businessId, query), HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
    }

    [Fact]
    public async Task Listing_staff_of_an_unknown_business_is_404()
    {
        await Json.ErrorAsync(await List(await fixture.Sql.InsertUserAsync(), Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Listing_staff_without_a_token_is_401()
    {
        var (_, businessId) = await BusinessAsync();

        await Json.ErrorAsync(await fixture.Api.CreateClient().GetAsync(ListPathFor(businessId)), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    // ------------------------------------------------------------ Add

    [Fact]
    public async Task New_email_gets_an_account_without_a_password_an_invite_and_a_staff_row()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();

        var body = await Json.ReadAsync(await Add(owner, businessId, $"  {email}  "), HttpStatusCode.Created);

        var userId = (await UserIdAsync(email))!.Value;
        AssertStaff(body, businessId, userId, "Sam Staff");
        Assert.Equal($"Sam Staff|<null>|America/New_York", Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT CONCAT_WS('|', name, IFNULL(password_hash, '<null>'), time_zone) FROM users WHERE id = @userId", new { userId })));
        var staffId = body.GetProperty("staffId").GetGuid();
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM staff WHERE id = @staffId AND user_id = @userId AND business_id = @businessId AND removed_at IS NULL",
            new { staffId, userId, businessId }));
        var inviteId = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM user_invites WHERE user_id = @userId AND created_by = @owner AND token_hash IS NULL", new { userId, owner }))!);

        var granted = Assert.Single(await AccessGrantedAsync(userId));
        Assert.Equal(businessId, granted.GetProperty("businessId").GetGuid());
        Assert.Equal("Staff", granted.GetProperty("role").GetString());
        Assert.Equal(inviteId, granted.GetProperty("inviteId").GetGuid());
        Assert.Equal(owner, granted.GetProperty("grantedByUserId").GetGuid());
    }

    [Fact]
    public async Task Existing_account_with_a_password_is_linked_and_told_without_an_invite()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash", name: "Existing Name");

        var body = await Json.ReadAsync(await Add(owner, businessId, email.ToUpperInvariant(), name: "Ignored Name"), HttpStatusCode.Created);

        AssertStaff(body, businessId, userId, "Existing Name"); // the name is used only for a new account
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId }));
        var granted = Assert.Single(await AccessGrantedAsync(userId));
        Assert.Equal(JsonValueKind.Null, granted.GetProperty("inviteId").ValueKind);
        Assert.Equal("Staff", granted.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Existing_account_without_a_password_gets_a_new_invite()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);
        await fixture.Sql.InsertInviteAsync(userId, createdAt: Sql.Now.AddDays(-1));

        await Json.ReadAsync(await Add(owner, businessId, email), HttpStatusCode.Created);

        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId }));
        var newest = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM user_invites WHERE user_id = @userId ORDER BY created_at DESC, id DESC LIMIT 1", new { userId }))!);
        Assert.Equal(newest, Assert.Single(await AccessGrantedAsync(userId)).GetProperty("inviteId").GetGuid());
    }

    [Fact]
    public async Task Owner_adds_themselves_without_an_event()
    {
        var email = ApiFixture.NewEmail();
        var owner = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash", name: "Olive Owner");
        var businessId = await fixture.Sql.InsertBusinessAsync(owner);

        var body = await Json.ReadAsync(await Add(owner, businessId, email), HttpStatusCode.Created);

        AssertStaff(body, businessId, owner, "Olive Owner");
        Assert.Empty(await AccessGrantedAsync(owner));
    }

    [Fact]
    public async Task Owner_of_another_business_can_be_staff_here_too()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var otherOwner = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");
        await fixture.Sql.InsertBusinessAsync(otherOwner);

        await Json.ReadAsync(await Add(owner, businessId, email), HttpStatusCode.Created);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM staff WHERE user_id = @otherOwner AND business_id = @businessId",
            new { otherOwner, businessId }));
    }

    [Fact]
    public async Task Account_already_staff_here_is_409_AlreadyStaff_and_nothing_changes()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);
        await fixture.Sql.InsertStaffAsync(businessId, userId);

        await Json.ErrorAsync(await Add(owner, businessId, email), HttpStatusCode.Conflict, "AlreadyStaff");

        Assert.Equal(1, await StaffRowsAsync(businessId));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId }));
        Assert.Empty(await AccessGrantedAsync(userId));
    }

    [Fact]
    public async Task Re_adding_a_removed_member_restores_the_same_row_with_its_services_and_hours()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");
        var staffId = await fixture.Sql.InsertStaffAsync(businessId, userId);
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        await fixture.Sql.AssignStaffToServiceAsync(staffId, serviceId);
        await fixture.Sql.ExecuteAsync(
            "INSERT INTO availability_rules (id, staff_id, day_of_week, start_local, end_local) VALUES (@id, @staffId, 'Monday', '09:00', '17:00')",
            new { id = Guid.NewGuid(), staffId });
        var bookingId = await fixture.Sql.InsertBookingAsync(new Sql.BookingFixture(businessId, staffId, serviceId, await fixture.Sql.InsertUserAsync()));
        await fixture.Sql.RemoveStaffAsync(staffId);

        var body = await Json.ReadAsync(await Add(owner, businessId, email), HttpStatusCode.Created);

        Assert.Equal(staffId, body.GetProperty("staffId").GetGuid());
        AssertStaff(body, businessId, userId, "Test User");
        Assert.Equal(1, await StaffRowsAsync(businessId));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM staff_services WHERE staff_id = @staffId", new { staffId }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM availability_rules WHERE staff_id = @staffId", new { staffId }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM bookings WHERE id = @bookingId AND staff_id = @staffId", new { bookingId, staffId }));
        Assert.Single(await AccessGrantedAsync(userId));
    }

    [Fact]
    public async Task Two_simultaneous_adds_of_a_new_email_give_one_201_and_one_409()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();

        var responses = await Task.WhenAll(Add(owner, businessId, email), Add(owner, businessId, email));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        await Json.ErrorAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "AlreadyStaff");
        var userId = (await UserIdAsync(email))!.Value;
        Assert.Equal(1, await StaffRowsAsync(businessId));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId }));
        Assert.Single(await AccessGrantedAsync(userId));
    }

    [Fact]
    public async Task Two_simultaneous_adds_of_an_existing_account_give_one_201_and_one_409()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");

        var responses = await Task.WhenAll(Add(owner, businessId, email), Add(owner, businessId, email));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await StaffRowsAsync(businessId));
        Assert.Single(await AccessGrantedAsync(userId));
    }

    [Fact]
    public async Task Two_simultaneous_restores_give_one_201_and_one_409()
    {
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email, passwordHash: "some-hash");
        await fixture.Sql.RemoveStaffAsync(await fixture.Sql.InsertStaffAsync(businessId, userId));

        var responses = await Task.WhenAll(Add(owner, businessId, email), Add(owner, businessId, email));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Single(await AccessGrantedAsync(userId));
    }

    [Fact]
    public async Task Account_created_by_a_sign_up_meanwhile_is_linked_on_the_retry()
    {
        // A sign-up for the same email commits while the add inserts its new account: users.email
        // refuses the add's row, and the retry links the signed-up account (a notice, no invite).
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var signedUp = Guid.NewGuid();
        Task<HttpResponseMessage> add;
        await using (var signUp = await fixture.Sql.BeginTransactionAsync())
        {
            await signUp.ExecuteAsync(
                "INSERT INTO users (id, email, password_hash, name, time_zone, created_at) " +
                "VALUES (@signedUp, @email, 'signed-up-hash', 'Signed Up', 'Europe/Paris', @now)",
                new { signedUp, email, now = Sql.Now });
            add = Add(owner, businessId, email);
            await fixture.Sql.WaitForLockWaitAsync("users"); // the add's INSERT waits on the email index
            Assert.False(add.IsCompleted);
            await signUp.CommitAsync();
        }

        var body = await Json.ReadAsync(await add, HttpStatusCode.Created);

        AssertStaff(body, businessId, signedUp, "Signed Up");
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM users WHERE email = @email", new { email }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @signedUp", new { signedUp }));
        Assert.Equal(JsonValueKind.Null, Assert.Single(await AccessGrantedAsync(signedUp)).GetProperty("inviteId").ValueKind);
    }

    [Fact]
    public async Task Add_waits_for_the_user_row_lock_and_then_sees_a_password_set_meanwhile()
    {
        // As create-owner (T6): an accept-invite holding the lock sets the password; the waiting
        // add must then send a notice, not a new invite.
        var (owner, businessId) = await BusinessAsync();
        var email = ApiFixture.NewEmail();
        var userId = await fixture.Sql.InsertUserAsync(email);
        Task<HttpResponseMessage> add;
        await using (var held = await fixture.Sql.LockUserRowAsync(userId))
        {
            add = Add(owner, businessId, email);
            await fixture.Sql.WaitForLockWaitAsync("users");
            await held.ExecuteAsync("UPDATE users SET password_hash = 'now-set' WHERE id = @userId", new { userId });
            await held.CommitAsync();
        }

        await Json.ReadAsync(await add, HttpStatusCode.Created);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId }));
        Assert.Equal(JsonValueKind.Null, Assert.Single(await AccessGrantedAsync(userId)).GetProperty("inviteId").ValueKind);
    }

    [Fact]
    public async Task Only_the_owner_may_add_staff()
    {
        var (_, businessId) = await BusinessAsync();
        var staffUser = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertStaffAsync(businessId, staffUser);
        var (otherOwner, _) = await BusinessAsync();

        foreach (var caller in new[] { staffUser, await fixture.Sql.InsertUserAsync(), otherOwner, await fixture.Sql.InsertAdminUserAsync() })
        {
            var email = ApiFixture.NewEmail();
            await Json.ErrorAsync(await Add(caller, businessId, email), HttpStatusCode.Forbidden, "Forbidden");
            Assert.Null(await UserIdAsync(email));
        }
        Assert.Equal(1, await StaffRowsAsync(businessId));
    }

    [Fact]
    public async Task Adding_to_an_unknown_business_is_404()
    {
        var (owner, _) = await BusinessAsync();
        var email = ApiFixture.NewEmail();

        await Json.ErrorAsync(await Add(owner, Guid.NewGuid(), email), HttpStatusCode.NotFound, "NotFound");

        Assert.Null(await UserIdAsync(email));
    }

    [Theory]
    [InlineData("name", """{"email":"a@example.test"}""")]
    [InlineData("name", """{"name":"   ","email":"a@example.test"}""")]
    [InlineData("name", """{"name":null,"email":"a@example.test"}""")]
    [InlineData("email", """{"name":"Sam"}""")]
    [InlineData("email", """{"name":"Sam","email":"not-an-email"}""")]
    [InlineData("email", """{"name":"Sam","email":null}""")]
    public async Task Invalid_add_returns_400_naming_the_field(string field, string json)
    {
        var (owner, businessId) = await BusinessAsync();

        var error = await Json.ErrorAsync(await fixture.Api.ClientFor(owner).PostAsync(ListPathFor(businessId), Json.Body(json)),
            HttpStatusCode.BadRequest, "ValidationFailed");

        Assert.Contains(field, Json.FieldNames(error));
        Assert.Equal(0, await StaffRowsAsync(businessId));
    }

    // ------------------------------------------------------------ Services

    private async Task<HashSet<Guid>> AssignedAsync(Guid staffId)
    {
        var count = await CountAsync("SELECT COUNT(*) FROM staff_services WHERE staff_id = @staffId", new { staffId });
        var set = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            set.Add(Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
                "SELECT service_id FROM staff_services WHERE staff_id = @staffId ORDER BY service_id LIMIT 1 OFFSET " + i, new { staffId }))!));
        }
        return set;
    }

    private static List<Guid> ServiceIds(JsonElement body) =>
        body.GetProperty("serviceIds").EnumerateArray().Select(e => e.GetGuid()).ToList();

    [Fact]
    public async Task Owner_replaces_a_staff_member_s_services()
    {
        var (owner, businessId) = await BusinessAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId);
        var old = await fixture.Sql.InsertServiceAsync(businessId, "Old");
        var colour = await fixture.Sql.InsertServiceAsync(businessId, "colour");
        var beard = await fixture.Sql.InsertServiceAsync(businessId, "Beard");
        await fixture.Sql.AssignStaffToServiceAsync(staffId, old);

        var body = await Json.ReadAsync(await SetServices(owner, staffId, colour, beard), HttpStatusCode.OK);

        Assert.Equal([beard, colour], ServiceIds(body)); // by name, ignoring case
        Assert.Single(body.EnumerateObject());
        Assert.Equal(new HashSet<Guid> { beard, colour }, await AssignedAsync(staffId));
    }

    [Fact]
    public async Task Assigned_staff_appear_on_the_service()
    {
        // Story: slot viewing for each selected service includes that staff member (ServiceDetail.staff here; slots in T13).
        var (owner, businessId) = await BusinessAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId);
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);

        await Json.ReadAsync(await SetServices(owner, staffId, serviceId), HttpStatusCode.OK);

        var service = await Json.ReadAsync(await fixture.Api.ClientFor(owner).GetAsync($"/v1/services/{serviceId}"), HttpStatusCode.OK);
        Assert.Equal(staffId, Assert.Single(service.GetProperty("staff").EnumerateArray()).GetProperty("staffId").GetGuid());
    }

    [Fact]
    public async Task Empty_list_removes_every_service()
    {
        var (owner, businessId) = await BusinessAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId);
        await fixture.Sql.AssignStaffToServiceAsync(staffId, await fixture.Sql.InsertServiceAsync(businessId));

        var body = await Json.ReadAsync(await SetServices(owner, staffId), HttpStatusCode.OK);

        Assert.Empty(ServiceIds(body));
        Assert.Empty(await AssignedAsync(staffId));
    }

    [Fact]
    public async Task Removed_staff_member_s_services_may_be_set()
    {
        var (owner, businessId) = await BusinessAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId);
        await fixture.Sql.RemoveStaffAsync(staffId);
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);

        await Json.ReadAsync(await SetServices(owner, staffId, serviceId), HttpStatusCode.OK);

        Assert.Equal(new HashSet<Guid> { serviceId }, await AssignedAsync(staffId));
    }

    [Fact]
    public async Task Changing_services_leaves_existing_bookings_alone()
    {
        var booking = await fixture.Sql.InsertBookingFixtureAsync();
        await fixture.Sql.AssignStaffToServiceAsync(booking.StaffId, booking.ServiceId);
        var owner = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT owner_user_id FROM businesses WHERE id = @id", new { id = booking.BusinessId }))!);
        var bookingId = await fixture.Sql.InsertBookingAsync(booking);
        const string row = "SELECT CONCAT_WS('|', staff_id, service_id, start_utc, end_utc, status, version) FROM bookings WHERE id = @bookingId";
        var before = await fixture.Sql.ScalarAsync(row, new { bookingId });

        await Json.ReadAsync(await SetServices(owner, booking.StaffId), HttpStatusCode.OK);

        Assert.Equal(before, await fixture.Sql.ScalarAsync(row, new { bookingId }));
    }

    [Fact]
    public async Task Service_of_another_business_or_unknown_or_repeated_is_400_and_nothing_changes()
    {
        var (owner, businessId) = await BusinessAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId);
        var mine = await fixture.Sql.InsertServiceAsync(businessId);
        await fixture.Sql.AssignStaffToServiceAsync(staffId, mine);
        var (_, otherBusiness) = await BusinessAsync();
        var theirs = await fixture.Sql.InsertServiceAsync(otherBusiness);

        foreach (var ids in new[] { new[] { mine, theirs }, [Guid.NewGuid()], [mine, mine] })
        {
            var error = await Json.ErrorAsync(await SetServices(owner, staffId, ids), HttpStatusCode.BadRequest, "ValidationFailed");
            Assert.Contains("serviceIds", Json.FieldNames(error));
        }

        Assert.Equal(new HashSet<Guid> { mine }, await AssignedAsync(staffId));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"serviceIds":null}""")]
    [InlineData("""{"serviceIds":["not-a-guid"]}""")]
    [InlineData("""{"serviceIds":[null]}""")]
    [InlineData("""{"serviceIds":[],"extra":1}""")]
    public async Task Malformed_services_body_is_400(string json)
    {
        var (owner, businessId) = await BusinessAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId);

        await Json.ErrorAsync(await SetServicesJson(owner, staffId, json), HttpStatusCode.BadRequest, "ValidationFailed");
    }

    [Fact]
    public async Task Only_the_owner_may_set_services()
    {
        var (_, businessId) = await BusinessAsync();
        var staffUser = await fixture.Sql.InsertUserAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId, staffUser);
        var serviceId = await fixture.Sql.InsertServiceAsync(businessId);
        var (otherOwner, _) = await BusinessAsync();

        foreach (var caller in new[] { staffUser, await fixture.Sql.InsertUserAsync(), otherOwner, await fixture.Sql.InsertAdminUserAsync() })
        {
            await Json.ErrorAsync(await SetServices(caller, staffId, serviceId), HttpStatusCode.Forbidden, "Forbidden");
        }
        Assert.Empty(await AssignedAsync(staffId));
    }

    [Fact]
    public async Task Setting_services_of_unknown_staff_is_404()
    {
        var (owner, _) = await BusinessAsync();

        await Json.ErrorAsync(await SetServices(owner, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Replacement_waits_for_the_staff_row_lock_and_then_replaces_everything()
    {
        // Another replacement holding the lock adds a link; the waiting one must still end with
        // exactly its own set, not the union.
        var (owner, businessId) = await BusinessAsync();
        var staffId = await fixture.Sql.InsertStaffAsync(businessId);
        var mine = await fixture.Sql.InsertServiceAsync(businessId, "Mine");
        var theirs = await fixture.Sql.InsertServiceAsync(businessId, "Theirs");
        Task<HttpResponseMessage> put;
        await using (var held = await fixture.Sql.LockRowAsync("staff", staffId))
        {
            put = SetServices(owner, staffId, mine);
            await fixture.Sql.WaitForLockWaitAsync("staff");
            await held.ExecuteAsync("INSERT INTO staff_services (staff_id, service_id) VALUES (@staffId, @theirs)", new { staffId, theirs });
            await held.CommitAsync();
        }

        await Json.ReadAsync(await put, HttpStatusCode.OK);

        Assert.Equal(new HashSet<Guid> { mine }, await AssignedAsync(staffId));
    }
}
