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

    /// <summary>
    /// R6: the staff member's Confirmed bookings whose blocked interval (buffers included, R1) overlaps
    /// <paramref name="interval"/> and that have not ended, so a change to the past never cancels bookings that
    /// already happened (decided 2026-11-20). Read under the staff lock.
    /// </summary>
    public Task<List<Booking>> AffectedAsync(Guid staffId, UtcInterval interval, DateTime nowUtc, CancellationToken ct)
    {
        var from = interval.Start - ServiceLimits.MaxBlockedSpan;
        return db.Bookings.AsNoTracking()
            .Where(b => b.StaffId == staffId && b.Status == BookingStatus.Confirmed && b.EndUtc > nowUtc
                && b.BlockedStartUtc > from && b.BlockedStartUtc < interval.End && b.BlockedEndUtc > interval.Start)
            .ToListAsync(ct);
    }

    /// <summary>
    /// R6, R8 (T19): the staff member's Confirmed bookings that have not started (start after now) and no longer fit
    /// the availability <paramref name="blocksOn"/> gives for their business-local date (<see cref="AvailabilityFit"/>).
    /// <paramref name="blocksOn"/> returns null for a date the change doesn't govern, whose bookings are skipped.
    /// <paramref name="onlyDate"/> narrows the read to bookings on one date. Read under the staff lock.
    /// </summary>
    public async Task<List<Booking>> OutsideAvailabilityAsync(Guid staffId, DateTimeZone zone, DateTime nowUtc, DateOnly? onlyDate,
        Func<DateOnly, IReadOnlyCollection<(TimeOnly Start, TimeOnly End)>?> blocksOn, CancellationToken ct)
    {
        var query = db.Bookings.AsNoTracking().Where(b => b.StaffId == staffId && b.Status == BookingStatus.Confirmed && b.StartUtc > nowUtc);
        if (onlyDate is { } date)
        {
            // A day either side covers every UTC offset (at most ±18 h), with plain arithmetic, so no date the path
            // accepts overflows; the exact local date is checked below.
            var from = date > DateOnly.MinValue ? date.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : DateTime.MinValue;
            var to = date < DateOnly.MaxValue.AddDays(-1) ? date.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : DateTime.MaxValue;
            query = query.Where(b => b.StartUtc >= from && b.StartUtc < to);
        }

        return Outside(await query.ToListAsync(ct), zone, onlyDate, blocksOn);
    }

    /// <summary>
    /// The bookings, of those given, that don't fit the availability <paramref name="blocksOn"/> gives for their
    /// business-local date (null: a date the change doesn't govern), on <paramref name="onlyDate"/> if set. Pure, so a
    /// caller that has loaded the bookings already (the time zone change, T22) makes no further queries.
    /// </summary>
    public static List<Booking> Outside(IEnumerable<Booking> bookings, DateTimeZone zone, DateOnly? onlyDate,
        Func<DateOnly, IReadOnlyCollection<(TimeOnly Start, TimeOnly End)>?> blocksOn)
    {
        var outside = new List<Booking>();
        foreach (var booking in bookings)
        {
            var bookingDate = AvailabilityFit.LocalDate(zone, booking.StartUtc);
            if (onlyDate is not null && bookingDate != onlyDate) continue;
            if (blocksOn(bookingDate) is { } blocks && !AvailabilityFit.Fits(zone, bookingDate, blocks, booking.StartUtc, booking.EndUtc))
                outside.Add(booking);
        }
        return outside;
    }

    /// <summary>
    /// R6 (T20): the staff member's Confirmed bookings that have not started (start after now), however far ahead.
    /// Read under the staff lock.
    /// </summary>
    public Task<List<Booking>> NotStartedAsync(Guid staffId, DateTime nowUtc, CancellationToken ct) =>
        db.Bookings.AsNoTracking()
            .Where(b => b.StaffId == staffId && b.Status == BookingStatus.Confirmed && b.StartUtc > nowUtc)
            .ToListAsync(ct);

    /// <summary>ADR 0004: [start - before-buffer, end + after-buffer), from the service's buffers now.</summary>
    public static UtcInterval BlockedInterval(DateTime startUtc, Service service) => new(
        startUtc.AddMinutes(-service.BufferBeforeMinutes),
        startUtc.AddMinutes(service.DurationMinutes + service.BufferAfterMinutes));
}
