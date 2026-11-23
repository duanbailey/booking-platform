using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using BookingPlatform.Api.Paging;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace BookingPlatform.Api.Controllers;

/// <summary>The Staff schema.</summary>
public sealed record StaffResponse(Guid StaffId, Guid BusinessId, Guid UserId, string Name, DateTime? RemovedAt);

/// <summary>POST /v1/businesses/{businessId}/staff body. The name is used only when a new account is created.</summary>
public sealed record AddStaffRequest([Required, PersonName] string Name, [Required, Email] string Email);

/// <summary>PUT /v1/staff/{staffId}/services body: the complete new set.</summary>
public sealed record SetStaffServicesRequest([Required] List<Guid>? ServiceIds);

/// <summary>The PUT response: the assigned services, by name (ignoring case and accents), then id.</summary>
public sealed record StaffServicesResponse(IReadOnlyList<Guid> ServiceIds);

/// <summary>POST /v1/staff/{staffId}/remove body, optional: the bookings shown in a previous 409 (R6).</summary>
public sealed record RemoveStaffRequest(ConfirmRequest? Confirm);

/// <summary>
/// Staff (stories "Owner adds staff", "Owner removes staff", "Owner assigns staff to services"). Any
/// signed-in user may list a business's active staff; only the owner may add or remove staff or set their
/// services: 404 if the business or staff member does not exist, otherwise 403 (specs/07-nfr.md).
/// </summary>
[ApiController]
[Route("v1")]
public sealed class StaffController(
    BookingDbContext db, OutboxWriter outbox, TimeProvider time, BookingScheduler scheduler, BookingCanceller canceller) : ControllerBase
{
    private const int MySqlDuplicateKey = 1062;

    /// <summary>A row of the staff list query (column names match the properties).</summary>
    private sealed record StaffRow(Guid StaffId, string Name);

    /// <summary>
    /// Active staff, ordered by name (the users.name collation: ignoring case and accents), then
    /// staffId, in the ORDER BY and the keyset alike.
    /// </summary>
    [HttpGet("businesses/{businessId:guid}/staff")]
    public async Task<Page<StaffSummaryResponse>> List(Guid businessId, [FromQuery] PageQuery page, CancellationToken ct)
    {
        if (!await db.Businesses.AnyAsync(b => b.Id == businessId, ct)) throw ApiException.NotFound();

        var take = page.Limit + 1;
        var after = page.AfterNameId();
        var rows = after is null
            ? await db.Database.SqlQuery<StaffRow>($"""
                SELECT s.id AS StaffId, u.name AS Name FROM staff s JOIN users u ON u.id = s.user_id
                WHERE s.business_id = {businessId} AND s.removed_at IS NULL
                ORDER BY u.name, s.id LIMIT {take}
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<StaffRow>($"""
                SELECT s.id AS StaffId, u.name AS Name FROM staff s JOIN users u ON u.id = s.user_id
                WHERE s.business_id = {businessId} AND s.removed_at IS NULL
                  AND (u.name > {after.Name} OR (u.name = {after.Name} AND s.id > {after.Id}))
                ORDER BY u.name, s.id LIMIT {take}
                """).ToListAsync(ct);

        return PageBuilder.From(rows, page.Limit, r => new NameIdCursor(r.Name, r.StaffId).Encode(), r => new StaffSummaryResponse(r.StaffId, r.Name));
    }

    /// <summary>
    /// Owner only. Links the account with this email, or creates one without a password (in the
    /// business's time zone); an account without a password gets a new invite. A removed staff
    /// row is restored, keeping its service links and availability. Emits AccessGranted, except
    /// when the owner adds themselves (decided 2026-11-19).
    /// </summary>
    [HttpPost("businesses/{businessId:guid}/staff")]
    public async Task<IActionResult> Add(Guid businessId, AddStaffRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        var business = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => new { b.OwnerUserId, b.TimeZone }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        Access.Require(canView: true, canAct: business.OwnerUserId == callerId);

        var email = request.Email.Trim();
        // A new email is created without a lock; if a simultaneous request (another add, or a
        // sign-up) creates the account first, users.email's unique index refuses ours, and one
        // retry takes the existing-account path, which settles AlreadyStaff under the lock.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var staff = await AddOnceAsync(businessId, business.OwnerUserId, business.TimeZone, request.Name.Trim(), email, ct);
                return StatusCode(StatusCodes.Status201Created, staff);
            }
            catch (DbUpdateException e) when (attempt == 1 && e.InnerException is MySqlException { Number: MySqlDuplicateKey })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<StaffResponse> AddOnceAsync(
        Guid businessId, Guid ownerId, string businessTimeZone, string name, string email, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // An existing account is locked first (as create-owner and accept-invite do), so its password
        // can't be set between this check and the invite, and two adds for it run one after the other.
        var existingId = await db.Users.Where(u => u.Email == email).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        var user = existingId is null ? null : await db.LockUserAsync(existingId.Value, ct);
        Staff? staff = null;
        if (user is null)
        {
            user = new User { Id = Guid.NewGuid(), Email = email, Name = name, TimeZone = businessTimeZone, CreatedAt = now };
            db.Users.Add(user);
        }
        else
        {
            // A locking read: a plain one would use the snapshot from before the lock wait (REPEATABLE READ).
            staff = await db.Staff
                .FromSql($"SELECT * FROM staff WHERE user_id = {user.Id} AND business_id = {businessId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (staff is { RemovedAt: null }) throw ApiException.AlreadyStaff();
        }

        if (staff is null)
        {
            staff = new Staff { Id = Guid.NewGuid(), UserId = user.Id, BusinessId = businessId, CreatedAt = now };
            db.Staff.Add(staff);
        }
        else
        {
            staff.RemovedAt = null; // restored: same row, so earlier bookings, services and hours stay linked
        }

        if (user.Id != ownerId)
        {
            // No password yet (new or never activated): a new invite; only the newest works (T7).
            UserInvite? invite = null;
            if (user.PasswordHash is null)
            {
                invite = new UserInvite { Id = Guid.NewGuid(), UserId = user.Id, CreatedBy = ownerId, CreatedAt = now };
                db.UserInvites.Add(invite);
            }
            outbox.Add(new AccessGranted(user.Id, businessId, GrantedRole.Staff, invite?.Id, ownerId));
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new StaffResponse(staff.Id, businessId, user.Id, user.Name, null);
    }

    /// <summary>
    /// Owner only (story "Owner removes staff", R6). The member's Confirmed bookings that have not started go
    /// through the confirm pattern; on confirmation they are cancelled with reason StaffRemoved in the same
    /// transaction that sets removed_at (the row is kept, with its services, availability and time off, so
    /// restoring brings them back). Decisions 2026-11-23: a removed member is a 200 no-op; the owner may remove
    /// their own staff row; no event for the removal itself. Locks: the owner's row FOR SHARE (cancelled_by
    /// references it), then the staff row (ADR 0002); everything after is read under them.
    /// </summary>
    [HttpPost("staff/{staffId:guid}/remove")]
    public async Task<StaffResponse> Remove(
        Guid staffId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RemoveStaffRequest? request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        // Before any lock, so a caller who may not do this never holds anyone up. The owner never changes.
        var ownerId = await (from s in db.Staff join b in db.Businesses on s.BusinessId equals b.Id where s.Id == staffId select (Guid?)b.OwnerUserId)
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        Access.Require(canView: true, canAct: ownerId == callerId);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.LockUsersAsync([(callerId, false)], ct) is not null) throw ApiException.Unauthenticated();
        var staff = await db.LockStaffAsync(staffId, ct) ?? throw ApiException.NotFound();
        var name = await db.Users.Where(u => u.Id == staff.UserId).Select(u => u.Name).SingleAsync(ct);
        if (staff.RemovedAt is not null) // already removed: nothing to do (decided 2026-11-23)
            return new StaffResponse(staff.Id, staff.BusinessId, staff.UserId, name, staff.RemovedAt);

        var now = time.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % 10)); // datetime(6): the response shows exactly what is stored
        var affected = await scheduler.NotStartedAsync(staffId, now, ct);
        ConfirmGate.Require(affected.Select(b => new AffectedBooking(b.Id, b.StaffId, b.StartUtc, b.EndUtc)).ToList(),
            request?.Confirm?.AffectedBookingIds, "R6",
            $"This staff member has {affected.Count} booking(s) that have not started; confirming removes them, cancels those bookings and notifies everyone affected.");

        if (affected.Count > 0)
        {
            var business = await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == staff.BusinessId, ct);
            // R6: business-initiated, so the R4 window doesn't apply.
            await canceller.CancelAsync(affected, business, callerId, ActorRole.Owner, CancellationReason.StaffRemoved, now, ct);
        }
        await db.Staff.Where(s => s.Id == staffId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RemovedAt, now), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new StaffResponse(staff.Id, staff.BusinessId, staff.UserId, name, now);
    }

    /// <summary>
    /// Owner only. Replaces the staff member's services with exactly these, which must all belong
    /// to the staff member's business (otherwise 400 naming serviceIds). Allowed for a removed
    /// staff member (decided 2026-11-19). The staff row is locked, so two replacements never
    /// interleave into a union. Existing bookings are unaffected.
    /// </summary>
    [HttpPut("staff/{staffId:guid}/services")]
    public async Task<StaffServicesResponse> SetServices(Guid staffId, SetStaffServicesRequest request, CancellationToken ct)
    {
        var serviceIds = request.ServiceIds!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var staff = await db.LockStaffAsync(staffId, ct) ?? throw ApiException.NotFound();
        var ownerId = await db.Businesses.Where(b => b.Id == staff.BusinessId).Select(b => b.OwnerUserId).SingleAsync(ct);
        Access.Require(canView: true, canAct: ownerId == User.GetUserId());

        if (serviceIds.Distinct().Count() != serviceIds.Count)
        {
            throw ApiException.ValidationFailed("serviceIds", "Must not repeat a service.");
        }
        var inBusiness = await db.Services.CountAsync(s => serviceIds.Contains(s.Id) && s.BusinessId == staff.BusinessId, ct);
        if (inBusiness != serviceIds.Count)
        {
            throw ApiException.ValidationFailed("serviceIds", "Every service must exist in the staff member's business.");
        }

        await db.StaffServices.Where(l => l.StaffId == staffId).ExecuteDeleteAsync(ct);
        db.StaffServices.AddRange(serviceIds.Select(id => new StaffService { StaffId = staffId, ServiceId = id }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var ordered = await db.Services.Where(s => serviceIds.Contains(s.Id))
            .OrderBy(s => s.Name).ThenBy(s => s.Id).Select(s => s.Id).ToListAsync(ct);
        return new StaffServicesResponse(ordered);
    }
}
