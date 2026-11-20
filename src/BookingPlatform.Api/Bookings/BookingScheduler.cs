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
    public Task<bool> OverlapsActiveBookingAsync(Guid staffId, UtcInterval blocked, CancellationToken ct)
    {
        var from = blocked.Start - ServiceLimits.MaxBlockedSpan;
        return db.Bookings.AnyAsync(b => b.StaffId == staffId && b.Status == BookingStatus.Confirmed
            && b.BlockedStartUtc > from && b.BlockedStartUtc < blocked.End && b.BlockedEndUtc > blocked.Start, ct);
    }

    /// <summary>ADR 0004: [start - before-buffer, end + after-buffer), from the service's buffers now.</summary>
    public static UtcInterval BlockedInterval(DateTime startUtc, Service service) => new(
        startUtc.AddMinutes(-service.BufferBeforeMinutes),
        startUtc.AddMinutes(service.DurationMinutes + service.BufferAfterMinutes));
}
