using BookingPlatform.Api.Data;
using BookingPlatform.Api.Slots;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace BookingPlatform.Api.Bookings;

/// <summary>
/// The schedule checks a booking (T14) and a reschedule (T17) make under the ADR 0002 staff lock. They
/// read through the caller's context, so the caller's transaction decides what they see.
/// </summary>
public sealed class BookingScheduler(BookingDbContext db)
{
    /// <summary>
    /// The slot starting at <paramref name="startUtc"/> if slot generation would offer it for this staff
    /// member and service with the minimum notice ignored and bookings left out (R8): on a step of an
    /// availability block, within it, not in the past, within the advance window, and with a blocked
    /// interval clear of time off. Null otherwise. Notice (R3) and bookings (R1) are the caller's checks,
    /// so each gets its own error.
    /// </summary>
    public async Task<OpenSlot?> OfferedSlotAsync(
        DateTimeZone zone, Business business, Service service, Guid staffId, DateTime startUtc, DateTime nowUtc, CancellationToken ct)
    {
        // Past or beyond the window: decided before any local-time arithmetic, which a far-off date would overflow.
        if (startUtc < nowUtc || startUtc > nowUtc.AddDays(business.MaxAdvanceDays)) return null;

        var settings = new SlotSettings(business.MinNoticeMinutes, business.MaxAdvanceDays, business.SlotStepMinutes);
        var slotService = new SlotService(service.DurationMinutes, service.BufferBeforeMinutes, service.BufferAfterMinutes);
        var date = Instant.FromDateTimeUtc(startUtc).InZone(zone).Date.ToDateOnly();
        var blocked = BlockedInterval(startUtc, service);

        var weekly = await db.AvailabilityRules.AsNoTracking().Where(r => r.StaffId == staffId).ToListAsync(ct);
        var exceptions = await db.AvailabilityExceptions.AsNoTracking().Where(e => e.StaffId == staffId && e.Date == date).ToListAsync(ct);
        var timeOff = await db.TimeOff.AsNoTracking()
            .Where(t => t.StaffId == staffId && t.StartUtc < blocked.End && t.EndUtc > blocked.Start)
            .Select(t => new UtcInterval(t.StartUtc, t.EndUtc)).ToListAsync(ct);

        var slots = SlotGenerator.Generate(zone, settings, slotService, date, date, nowUtc, ignoreMinimumNotice: true,
            [new StaffSchedule(staffId, weekly, exceptions, timeOff)]);
        return slots.FirstOrDefault(s => s.StartUtc == startUtc);
    }

    /// <summary>
    /// R1: whether a Confirmed booking of this staff member has a blocked interval overlapping
    /// <paramref name="blocked"/> (half-open, so touching is fine). Stored blocked intervals are compared,
    /// never the service's current buffers (ADR 0004).
    /// </summary>
    /// <param name="exceptBookingId">A booking being moved: its own current slot never conflicts (ADR 0003).</param>
    public Task<bool> OverlapsActiveBookingAsync(Guid staffId, UtcInterval blocked, CancellationToken ct, Guid? exceptBookingId = null)
    {
        var from = blocked.Start - ServiceLimits.MaxBlockedSpan;
        return db.Bookings.AnyAsync(b => b.StaffId == staffId && b.Status == BookingStatus.Confirmed && b.Id != exceptBookingId
            && b.BlockedStartUtc > from && b.BlockedStartUtc < blocked.End && b.BlockedEndUtc > blocked.Start, ct);
    }

    /// <summary>
    /// R4: the customer's bookings with this business that they can no longer cancel. Decided 2026-11-20:
    /// Confirmed, with any staff member or service of the business, not started, and starting less than the
    /// cancellation window from now (exactly N hours away may still be cancelled, so it is outside).
    /// <para>
    /// Concurrency (code review, 2026-11-20). Callers hold the customer's user row FOR UPDATE. Every operation
    /// that adds a booking for a customer or moves one of theirs takes that lock too (creation here; reschedule
    /// must, T17), so no booking can enter the window meanwhile. A booking can still be cancelled or moved out
    /// under its own staff member's lock alone, so each candidate from the snapshot is read again with
    /// FOR SHARE by primary key: that sees its current values and holds it until the caller commits, so a listed
    /// or notified booking can't be cancelled in between. Primary-key locks avoid gap locks on the
    /// (customer_id, start_utc) range, which would block other customers' bookings.
    /// </para>
    /// </summary>
    public async Task<List<AffectedBooking>> InsideCancellationWindowAsync(Guid customerId, Business business, DateTime nowUtc, CancellationToken ct)
    {
        var windowEnd = nowUtc.AddHours(business.CancellationWindowHours);
        var candidates = await (
                from b in db.Bookings
                join s in db.Staff on b.StaffId equals s.Id
                where b.CustomerId == customerId && s.BusinessId == business.Id && b.Status == BookingStatus.Confirmed
                    && b.StartUtc > nowUtc && b.StartUtc < windowEnd
                select b.Id)
            .ToListAsync(ct);

        var inside = new List<AffectedBooking>();
        foreach (var id in candidates)
        {
            var current = await db.Bookings.FromSql($"SELECT * FROM bookings WHERE id = {id} FOR SHARE").AsNoTracking().SingleAsync(ct);
            if (current.Status == BookingStatus.Confirmed && current.StartUtc > nowUtc && current.StartUtc < windowEnd)
                inside.Add(new AffectedBooking(current.Id, current.StaffId, current.StartUtc, current.EndUtc));
        }
        return inside;
    }

    /// <summary>ADR 0004: [start - before-buffer, end + after-buffer), from the service's buffers now.</summary>
    public static UtcInterval BlockedInterval(DateTime startUtc, Service service) => new(
        startUtc.AddMinutes(-service.BufferBeforeMinutes),
        startUtc.AddMinutes(service.DurationMinutes + service.BufferAfterMinutes));
}
