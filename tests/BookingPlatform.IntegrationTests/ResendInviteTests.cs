using System.Net;
using System.Text.Json;
using BookingPlatform.IntegrationTests.Infrastructure;
using BookingPlatform.Tests.Shared;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T7: POST /v1/users/{userId}/resend-invite. Story "Invited user sets their password"
/// (resend scenarios): an admin resends an owner's invite, an owner their staff's; a new
/// invite is queued as AccessGranted and only the newest link works; an account with a
/// password gets InviteNotPending. Any other caller gets 404, before the 422, so the reply
/// never reveals whether the user exists or has a password (specs/04-api.openapi.yaml).
/// </summary>
public class ResendInviteTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static string PathFor(Guid userId) => $"/v1/users/{userId}/resend-invite";

    private Task<HttpResponseMessage> Resend(Guid callerId, Guid userId) =>
        fixture.Api.ClientFor(callerId).PostAsync(PathFor(userId), null);

    private async Task<long> CountAsync(string sql, object parameters) =>
        Convert.ToInt64(await fixture.Sql.ScalarAsync(sql, parameters));

    private Task<long> InviteCountAsync(Guid userId) =>
        CountAsync("SELECT COUNT(*) FROM user_invites WHERE user_id = @userId", new { userId });

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
            events.Add(JsonDocument.Parse(payload).RootElement);
        }
        return events;
    }

    /// <summary>The user's newest invite row (by created_at, then id).</summary>
    private async Task<(Guid Id, Guid CreatedBy, bool HasToken)> NewestInviteAsync(Guid userId)
    {
        var id = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT id FROM user_invites WHERE user_id = @userId ORDER BY created_at DESC, id DESC LIMIT 1", new { userId }))!);
        var createdBy = Guid.Parse(Convert.ToString(await fixture.Sql.ScalarAsync(
            "SELECT created_by FROM user_invites WHERE id = @id", new { id }))!);
        var hasToken = await fixture.Sql.ScalarAsync("SELECT token_hash FROM user_invites WHERE id = @id", new { id }) is string;
        return (id, createdBy, hasToken);
    }

    /// <summary>An owner (with a password) of a business, with an invited (no password) staff member.</summary>
    private async Task<(Guid OwnerId, Guid BusinessId, Guid StaffUserId, Guid StaffId)> BusinessWithInvitedStaffAsync()
    {
        var ownerId = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        var businessId = await fixture.Sql.InsertBusinessAsync(ownerId);
        var staffUserId = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertInviteAsync(staffUserId, "first-token-" + staffUserId, createdBy: ownerId);
        var staffId = await fixture.Sql.InsertStaffAsync(businessId, staffUserId);
        return (ownerId, businessId, staffUserId, staffId);
    }

    /// <summary>An owner account created by an admin, with no password yet, and its business.</summary>
    private async Task<(Guid OwnerId, Guid BusinessId)> InvitedOwnerAsync(Guid adminId)
    {
        var ownerId = await fixture.Sql.InsertUserAsync();
        var businessId = await fixture.Sql.InsertBusinessAsync(ownerId);
        await fixture.Sql.InsertInviteAsync(ownerId, "first-token-" + ownerId, createdBy: adminId);
        return (ownerId, businessId);
    }

    private static void AssertGranted(JsonElement granted, Guid userId, Guid businessId, string role, Guid inviteId, Guid grantedBy)
    {
        var data = granted.GetProperty("data");
        Assert.Equal(userId.ToString(), data.GetProperty("userId").GetString());
        Assert.Equal(businessId.ToString(), data.GetProperty("businessId").GetString());
        Assert.Equal(role, data.GetProperty("role").GetString());
        Assert.Equal(inviteId.ToString(), data.GetProperty("inviteId").GetString());
        Assert.Equal(grantedBy.ToString(), data.GetProperty("grantedByUserId").GetString());
    }

    // ------------------------------------------------------------ Admin, for an owner

    [Fact]
    public async Task Admin_resends_an_owners_invite_as_a_new_unsent_invite_and_AccessGranted_for_Owner()
    {
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var (ownerId, businessId) = await InvitedOwnerAsync(adminId);

        var response = await Resend(adminId, ownerId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, await InviteCountAsync(ownerId));
        var newest = await NewestInviteAsync(ownerId);
        Assert.Equal(adminId, newest.CreatedBy);
        Assert.False(newest.HasToken, "the worker makes the token (T26)");
        AssertGranted(Assert.Single(await AccessGrantedAsync(ownerId)), ownerId, businessId, "Owner", newest.Id, adminId);
    }

    [Fact]
    public async Task After_a_resend_the_older_link_stops_working_and_the_new_one_works_once_sent()
    {
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var (ownerId, _) = await InvitedOwnerAsync(adminId);
        await Resend(adminId, ownerId);
        var newest = await NewestInviteAsync(ownerId);
        var anonymous = fixture.Api.CreateClient();

        await Json.ErrorAsync(await anonymous.PostJsonAsync("/v1/auth/accept-invite",
            new { token = "first-token-" + ownerId, password = ApiFixture.Password }), HttpStatusCode.BadRequest, "InviteInvalid");

        // The worker sends the new invite (T26).
        await fixture.Sql.ExecuteAsync(
            "UPDATE user_invites SET token_hash = @hash, expires_at = @expires WHERE id = @id",
            new { hash = Sql.TokenHash("second-token-" + ownerId), expires = DateTime.UtcNow.AddDays(7), id = newest.Id });
        await Json.ReadAsync(await anonymous.PostJsonAsync("/v1/auth/accept-invite",
            new { token = "second-token-" + ownerId, password = ApiFixture.Password }), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Each_resend_adds_another_invite_with_no_throttle()
    {
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var (ownerId, _) = await InvitedOwnerAsync(adminId);

        Assert.Equal(HttpStatusCode.NoContent, (await Resend(adminId, ownerId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Resend(adminId, ownerId)).StatusCode);

        Assert.Equal(3, await InviteCountAsync(ownerId));
        Assert.Equal(2, (await AccessGrantedAsync(ownerId)).Count);
    }

    [Fact]
    public async Task Admin_resending_for_an_owner_who_has_a_password_gets_422_InviteNotPending_and_nothing_changes()
    {
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var ownerId = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        await fixture.Sql.InsertBusinessAsync(ownerId);

        await Json.ErrorAsync(await Resend(adminId, ownerId), (HttpStatusCode)422, "InviteNotPending");

        Assert.Equal(0, await InviteCountAsync(ownerId));
        Assert.Empty(await AccessGrantedAsync(ownerId));
    }

    [Fact]
    public async Task Admin_resending_for_a_user_who_owns_no_business_gets_404()
    {
        // An admin may resend only owners' invites (specs/07-nfr.md); this one is staff elsewhere.
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var (_, _, staffUserId, _) = await BusinessWithInvitedStaffAsync();

        await Json.ErrorAsync(await Resend(adminId, staffUserId), HttpStatusCode.NotFound, "NotFound");

        Assert.Equal(1, await InviteCountAsync(staffUserId));
        Assert.Empty(await AccessGrantedAsync(staffUserId));
    }

    [Fact]
    public async Task Admin_resending_for_an_unknown_user_gets_404()
    {
        var adminId = await fixture.Sql.InsertAdminUserAsync();

        await Json.ErrorAsync(await Resend(adminId, Guid.NewGuid()), HttpStatusCode.NotFound, "NotFound");
    }

    // ------------------------------------------------------------ Owner, for staff

    [Fact]
    public async Task Owner_resends_a_staff_members_invite_as_AccessGranted_for_Staff_in_their_business()
    {
        var (ownerId, businessId, staffUserId, _) = await BusinessWithInvitedStaffAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await Resend(ownerId, staffUserId)).StatusCode);

        var newest = await NewestInviteAsync(staffUserId);
        Assert.Equal(ownerId, newest.CreatedBy);
        Assert.False(newest.HasToken);
        AssertGranted(Assert.Single(await AccessGrantedAsync(staffUserId)), staffUserId, businessId, "Staff", newest.Id, ownerId);
    }

    [Fact]
    public async Task Owner_resending_for_staff_who_has_a_password_gets_422_InviteNotPending()
    {
        var ownerId = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        var businessId = await fixture.Sql.InsertBusinessAsync(ownerId);
        var staffUserId = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        await fixture.Sql.InsertStaffAsync(businessId, staffUserId);

        await Json.ErrorAsync(await Resend(ownerId, staffUserId), (HttpStatusCode)422, "InviteNotPending");

        Assert.Equal(0, await InviteCountAsync(staffUserId));
        Assert.Empty(await AccessGrantedAsync(staffUserId));
    }

    [Fact]
    public async Task Owner_resending_for_removed_staff_gets_404()
    {
        var (ownerId, _, staffUserId, staffId) = await BusinessWithInvitedStaffAsync();
        await fixture.Sql.RemoveStaffAsync(staffId);

        await Json.ErrorAsync(await Resend(ownerId, staffUserId), HttpStatusCode.NotFound, "NotFound");

        Assert.Equal(1, await InviteCountAsync(staffUserId));
    }

    [Fact]
    public async Task Owner_resending_for_staff_of_another_business_gets_404()
    {
        var (_, _, staffUserId, _) = await BusinessWithInvitedStaffAsync();
        var otherOwner = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        await fixture.Sql.InsertBusinessAsync(otherOwner);

        await Json.ErrorAsync(await Resend(otherOwner, staffUserId), HttpStatusCode.NotFound, "NotFound");

        Assert.Equal(1, await InviteCountAsync(staffUserId));
    }

    [Fact]
    public async Task Staff_member_cannot_resend_a_colleagues_invite()
    {
        // specs/07-nfr.md: only the owner resends a staff member's invite.
        var (_, businessId, staffUserId, _) = await BusinessWithInvitedStaffAsync();
        var colleague = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        await fixture.Sql.InsertStaffAsync(businessId, colleague);

        await Json.ErrorAsync(await Resend(colleague, staffUserId), HttpStatusCode.NotFound, "NotFound");
    }

    // ------------------------------------------------------------ Both, and neither

    [Fact]
    public async Task Admin_who_is_also_the_owner_resends_for_a_user_who_owns_a_business_as_Owner()
    {
        // The user owns a business (made by an admin) and is staff in the caller's business.
        var callerId = await fixture.Sql.InsertAdminUserAsync();
        var callerBusiness = await fixture.Sql.InsertBusinessAsync(callerId);
        var (userId, userBusiness) = await InvitedOwnerAsync(callerId);
        await fixture.Sql.InsertStaffAsync(callerBusiness, userId);

        Assert.Equal(HttpStatusCode.NoContent, (await Resend(callerId, userId)).StatusCode);

        var newest = await NewestInviteAsync(userId);
        AssertGranted(Assert.Single(await AccessGrantedAsync(userId)), userId, userBusiness, "Owner", newest.Id, callerId);
    }

    [Fact]
    public async Task Owner_who_is_not_an_admin_resends_as_Staff_even_if_the_user_also_owns_a_business()
    {
        var ownerId = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        var ownerBusiness = await fixture.Sql.InsertBusinessAsync(ownerId);
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var (userId, _) = await InvitedOwnerAsync(adminId);
        await fixture.Sql.InsertStaffAsync(ownerBusiness, userId);

        Assert.Equal(HttpStatusCode.NoContent, (await Resend(ownerId, userId)).StatusCode);

        var newest = await NewestInviteAsync(userId);
        AssertGranted(Assert.Single(await AccessGrantedAsync(userId)), userId, ownerBusiness, "Staff", newest.Id, ownerId);
    }

    [Fact]
    public async Task Caller_with_no_right_gets_404_even_when_the_user_has_a_password()
    {
        // 404 is checked before 422, so the reply never reveals that the account has a password.
        var stranger = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        var withPassword = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");
        await fixture.Sql.InsertBusinessAsync(withPassword);

        await Json.ErrorAsync(await Resend(stranger, withPassword), HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Customer_gets_404_for_an_invited_owner()
    {
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var (ownerId, _) = await InvitedOwnerAsync(adminId);
        var customer = await fixture.Sql.InsertUserAsync(passwordHash: "not-a-real-hash");

        await Json.ErrorAsync(await Resend(customer, ownerId), HttpStatusCode.NotFound, "NotFound");

        Assert.Equal(1, await InviteCountAsync(ownerId));
    }

    [Fact]
    public async Task Without_a_token_the_request_gets_401()
    {
        await Json.ErrorAsync(await fixture.Api.CreateClient().PostAsync(PathFor(Guid.NewGuid()), null),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Resend_waits_for_the_user_row_lock_and_then_sees_a_password_set_meanwhile()
    {
        // An accept holding the lock commits a password: the waiting resend must see it.
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var (ownerId, _) = await InvitedOwnerAsync(adminId);
        Task<HttpResponseMessage> resend;
        await using (var held = await fixture.Sql.LockUserRowAsync(ownerId))
        {
            resend = Resend(adminId, ownerId);
            await fixture.Sql.WaitForLockWaitAsync("users");
            Assert.False(resend.IsCompleted);
            await held.ExecuteAsync("UPDATE users SET password_hash = 'set-meanwhile' WHERE id = @ownerId", new { ownerId });
            await held.CommitAsync();
        }

        await Json.ErrorAsync(await resend, (HttpStatusCode)422, "InviteNotPending");
        Assert.Equal(1, await InviteCountAsync(ownerId));
        Assert.Empty(await AccessGrantedAsync(ownerId));
    }

    [Fact]
    public async Task Resend_after_an_accept_commits_gets_InviteNotPending()
    {
        var adminId = await fixture.Sql.InsertAdminUserAsync();
        var ownerId = await fixture.Sql.InsertUserAsync();
        await fixture.Sql.InsertBusinessAsync(ownerId);
        await fixture.Sql.InsertInviteAsync(ownerId, "token-" + ownerId);

        await Json.ReadAsync(await fixture.Api.CreateClient().PostJsonAsync("/v1/auth/accept-invite",
            new { token = "token-" + ownerId, password = ApiFixture.Password }), HttpStatusCode.OK);

        await Json.ErrorAsync(await Resend(adminId, ownerId), (HttpStatusCode)422, "InviteNotPending");
        Assert.Equal(1, await InviteCountAsync(ownerId));
    }
}
