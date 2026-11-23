using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using BookingPlatform.Api.Paging;
using BookingPlatform.Api.Slots;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace BookingPlatform.Api.Controllers;

/// <summary>POST time-zone-change body: an IANA name (an alias is stored under its canonical name), and the confirmation (R6).</summary>
public sealed record ChangeTimeZoneRequest([Required, IanaTimeZone] string? TimeZone, ConfirmRequest? Confirm);

/// <summary>The TimeZoneChange schema (id added 2026-11-23).</summary>
public sealed record TimeZoneChangeResponse(Guid Id, Guid BusinessId, string OldTimeZone, string NewTimeZone, Guid ChangedBy, DateTime ChangedAt)
{
    public static TimeZoneChangeResponse From(TimeZoneChange c) => new(c.Id, c.BusinessId, c.OldTimeZone, c.NewTimeZone, c.ChangedBy, c.ChangedAt);
}

/// <summary>
/// Changing a business's time zone (R2; story "Owner changes the business's time zone"; T22) and its audit log.
/// Owner only: 404 for an unknown business, 403 for anyone else (any signed-in user can view a business).
/// </summary>
[ApiController]
[Route("v1/businesses/{businessId:guid}")]
public sealed class TimeZoneChangesController(
    BookingDbContext db, BookingCanceller canceller, OutboxWriter outbox, BusinessZones zones, TimeProvider time)
    : ControllerBase
{
    /// <summary>
    /// R2. Bookings keep their UTC times and availability keeps its local times, read in the new zone. Decisions
    /// 2026-11-23 (plan/tasks.md, T22):
    /// <list type="bullet">
    /// <item>The zone is canonicalized; the current zone (an alias too) is a 200 no-op: no record, no events.</item>
    /// <item>Confirmed bookings that have not started, of any staff member, that no longer fit their date's
    /// availability in the new zone (as T19) go through the confirm pattern (rule R6); on confirmation they are
    /// cancelled with AvailabilityChanged, their BookingCancelled carrying the old zone, the one they were booked in.</item>
    /// <item>Every other such booking whose local start or local end differs between the zones gets
    /// BookingLocalTimeChanged. A booking in progress is neither cancelled nor notified.</item>
    /// <item>One transaction: the canonical zone, the time_zone_changes row, the cancellations and the events.</item>
    /// </list>
    /// Locks (ADR 0002, amended 2026-11-23): the owner's row FOR SHARE (changed_by and cancelled_by reference it), the
    /// business row FOR UPDATE, every staff row of the business in id order, then each booking that gets
    /// BookingLocalTimeChanged FOR UPDATE (outbox ordering: the event writer holds its group's row).
    /// </summary>
    [HttpPost("time-zone-change")]
    public async Task<BusinessResponse> Change(Guid businessId, ChangeTimeZoneRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        var ownerId = await db.Businesses.Where(b => b.Id == businessId).Select(b => (Guid?)b.OwnerUserId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound();
        Access.Require(canView: true, canAct: ownerId == callerId); // before any lock; the owner never changes
        var newZoneName = IanaTimeZoneAttribute.Canonicalize(request.TimeZone!);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.LockUsersAsync([(callerId, false)], ct) is not null) throw ApiException.Unauthenticated();
        var business = await db.Businesses.FromSql($"SELECT * FROM businesses WHERE id = {businessId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
        // Already set: nothing happens. Compared canonically, since a business saved before zones were stored
        // canonically (T13) may still hold an alias such as UTC (T22 review).
        var currentName = IanaTimeZoneAttribute.IsIanaName(business.TimeZone) ? IanaTimeZoneAttribute.Canonicalize(business.TimeZone) : business.TimeZone;
        if (currentName == newZoneName) return BusinessResponse.From(business);
        // One statement: InnoDB locks them in (business_id, id) index order, i.e. by id.
        var staffIds = await db.Database
            .SqlQuery<Guid>($"SELECT id AS Value FROM staff WHERE business_id = {businessId} ORDER BY id FOR UPDATE").ToListAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % 10)); // datetime(6): the stored changed_at is exactly this
        var oldZone = zones.Resolve(business);
        var newZone = DateTimeZoneProviders.Tzdb[newZoneName];

        // R2, R6: everything that has not started is read under the locks, so no writer can change it meanwhile. A
        // fixed number of statements however big the business, since every booking of it waits on these locks (T22
        // review): the bookings once, the weekly rules once, the exceptions once, then one lock for the moved ones.
        var since = DateOnly.FromDateTime(now).AddDays(-1); // no booking that hasn't started is on an earlier local date
        var upcoming = await (from b in db.Bookings.AsNoTracking()
                              join s in db.Staff on b.StaffId equals s.Id
                              where s.BusinessId == businessId && b.Status == BookingStatus.Confirmed && b.StartUtc > now
                              select b).ToListAsync(ct);
        var weekly = (await db.AvailabilityRules.AsNoTracking().Where(r => staffIds.Contains(r.StaffId)).ToListAsync(ct))
            .GroupBy(r => r.StaffId).ToDictionary(g => g.Key, g => g.ToLookup(r => r.DayOfWeek));
        var exceptions = (await db.AvailabilityExceptions.AsNoTracking().Where(e => staffIds.Contains(e.StaffId) && e.Date >= since).ToListAsync(ct))
            .GroupBy(e => e.StaffId).ToDictionary(g => g.Key, g => g.ToLookup(e => e.Date));
        var noRules = Enumerable.Empty<AvailabilityRule>().ToLookup(r => r.DayOfWeek);
        var noExceptions = Enumerable.Empty<AvailabilityException>().ToLookup(e => e.Date);
        var outside = upcoming.GroupBy(b => b.StaffId).SelectMany(staff => BookingScheduler.Outside(staff, newZone, onlyDate: null,
                date => AvailabilityFit.BlocksOn(date, weekly.GetValueOrDefault(staff.Key, noRules), exceptions.GetValueOrDefault(staff.Key, noExceptions))))
            .ToList();
        ConfirmGate.Require(outside.Select(b => new AffectedBooking(b.Id, b.StaffId, b.StartUtc, b.EndUtc)).ToList(),
            request.Confirm?.AffectedBookingIds, "R6",
            $"This time zone change leaves {outside.Count} booking(s) outside availability; confirming cancels them and notifies everyone affected.");

        var outsideIds = outside.Select(b => b.Id).ToHashSet();
        var moved = upcoming.Where(b => !outsideIds.Contains(b.Id) && LocalTimeDiffers(b, oldZone, newZone)).ToList();
        if (moved.Count > 0)
        {
            // One statement, rows locked in primary-key order. EF can't expand a list parameter in raw SQL, so the
            // statement gets one placeholder per booking ({0}, {1}, ...) and the IDs go in as parameters; the
            // concatenated text is only those placeholders, never a value.
            var placeholders = string.Join(",", moved.Select((_, i) => $"{{{i}}}"));
#pragma warning disable EF1003 // see above: placeholders only
            await db.Database.ExecuteSqlRawAsync("SELECT id FROM bookings WHERE id IN (" + placeholders + ") FOR UPDATE",
                moved.Select(b => (object)b.Id), ct);
#pragma warning restore EF1003
        }

        // The cancellation events carry the old zone: the one the customer booked in (decided 2026-11-23).
        await canceller.CancelAsync(outside, business, callerId, ActorRole.Owner, CancellationReason.AvailabilityChanged, now, ct);
        await db.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(s => s.SetProperty(b => b.TimeZone, newZoneName), ct);
        var change = new TimeZoneChange
        {
            Id = Guid.NewGuid(), BusinessId = businessId, OldTimeZone = business.TimeZone, NewTimeZone = newZoneName, ChangedBy = callerId, ChangedAt = now,
        };
        db.TimeZoneChanges.Add(change);
        foreach (var booking in moved)
        {
            outbox.Add(new BookingLocalTimeChanged(booking.Id, businessId, booking.CustomerId,
                new EventSlot(booking.StaffId, booking.StartUtc, booking.EndUtc), business.TimeZone, newZoneName, change.Id));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        business.TimeZone = newZoneName;
        return BusinessResponse.From(business);
    }

    /// <summary>Owner only. Newest first: changedAt, then id, both descending; the (business_id, changed_at) index serves it.</summary>
    [HttpGet("time-zone-changes")]
    public async Task<Page<TimeZoneChangeResponse>> List(Guid businessId, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var ownerId = await db.Businesses.Where(b => b.Id == businessId).Select(b => (Guid?)b.OwnerUserId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound();
        Access.Require(canView: true, canAct: ownerId == User.GetUserId());
        var after = page.AfterStartId(StartIdList.TimeZoneChanges);

        var query = after is null
            ? db.TimeZoneChanges.Where(c => c.BusinessId == businessId)
            : db.TimeZoneChanges.FromSql(
                $"SELECT * FROM time_zone_changes WHERE business_id = {businessId} AND (changed_at < {after.Start} OR (changed_at = {after.Start} AND id < {after.Id}))");
        var fetched = await query.AsNoTracking().OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id).Take(page.Limit + 1).ToListAsync(ct);

        return PageBuilder.From(fetched, page.Limit, c => new StartIdCursor(c.ChangedAt, c.Id).Encode(StartIdList.TimeZoneChanges), TimeZoneChangeResponse.From);
    }

    /// <summary>R2: the booking's local start or local end (date and time) differs between the zones (decided 2026-11-23).</summary>
    private static bool LocalTimeDiffers(Booking booking, DateTimeZone oldZone, DateTimeZone newZone)
    {
        static LocalDateTime Local(DateTime utc, DateTimeZone zone) => Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).InZone(zone).LocalDateTime;
        return Local(booking.StartUtc, oldZone) != Local(booking.StartUtc, newZone) || Local(booking.EndUtc, oldZone) != Local(booking.EndUtc, newZone);
    }
}
