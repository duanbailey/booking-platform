using BookingPlatform.Api.Data;
using NodaTime;
using NodaTime.TimeZones;

namespace BookingPlatform.Api.Slots;

/// <summary>The business settings slot generation reads (R3, R8).</summary>
public sealed record SlotSettings(int MinNoticeMinutes, int MaxAdvanceDays, int SlotStepMinutes);

/// <summary>The service's duration and buffers, in minutes (R1, R8).</summary>
public sealed record SlotService(int DurationMinutes, int BufferBeforeMinutes, int BufferAfterMinutes);

/// <summary>A half-open UTC interval [Start, End).</summary>
public readonly record struct UtcInterval(DateTime Start, DateTime End)
{
    public bool Overlaps(UtcInterval other) => Start < other.End && other.Start < End;
}

/// <summary>
/// One staff member's schedule: weekly availability, date exceptions, and everything that
/// blocks them (time off, and the blocked intervals of Confirmed bookings).
/// </summary>
public sealed record StaffSchedule(
    Guid StaffId,
    IReadOnlyCollection<AvailabilityRule> Weekly,
    IReadOnlyCollection<AvailabilityException> Exceptions,
    IReadOnlyCollection<UtcInterval> Blocked);

/// <summary>An open slot: [StartUtc, EndUtc), excluding buffers (the Slot schema).</summary>
public sealed record OpenSlot(Guid StaffId, DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// The part of a requested date range that can hold an offered slot, and the UTC window every
/// such slot's blocked interval (R1, buffers included) falls in, so callers load only the time off
/// and bookings that can matter.
/// </summary>
public sealed record SlotPlan(DateOnly From, DateOnly To, UtcInterval BlockedWindow);

/// <summary>
/// Slot generation (R8, with R1, R2 and R3). Decisions 2026-11-20 (plan/tasks.md):
/// <list type="bullet">
/// <item>A slot is offered when start ≥ now + minimum notice (start ≥ now when the notice is
/// ignored, R3) and start ≤ now + maxAdvanceDays × 24 h (R8, inclusive, checked on the start).</item>
/// <item>A date's availability is its date exceptions if it has any (a row without times means
/// none), otherwise the weekly blocks for its local weekday (R8).</item>
/// <item>Starts step on the local wall clock from each block start (R8). Local times inside a
/// spring-forward gap are dropped; a repeated fall-back time is offered at its first occurrence
/// only (R2). Every slot lasts exactly the service duration in elapsed time.</item>
/// <item>Availability bounds the appointment, not its buffers (R8): the slot must end by the block
/// end, which, inside a gap, is the instant the gap ends and, when ambiguous, its later
/// occurrence, so the block covers the whole repeated hour. Each block bounds slots on its own, even
/// when two blocks touch: they are never merged (R8, decided 2026-11-20).</item>
/// <item>A slot whose blocked interval [start - before, end + after) overlaps anything blocking
/// the staff member (time off, Confirmed bookings' blocked intervals) is not offered (R1, R8).
/// Intervals are half-open, so touching is fine.</item>
/// </list>
/// T14 reuses this to check that a booking starts on an offered slot (R8).
/// </summary>
public static class SlotGenerator
{
    public static List<OpenSlot> Generate(
        DateTimeZone zone, SlotSettings settings, SlotService service, DateOnly from, DateOnly to,
        DateTime nowUtc, bool ignoreMinimumNotice, IEnumerable<StaffSchedule> staff)
    {
        var plan = Plan(zone, settings, service, from, to, nowUtc, ignoreMinimumNotice);
        if (plan is null) return [];
        var (earliest, latest) = StartBounds(settings, nowUtc, ignoreMinimumNotice);
        var duration = Duration.FromMinutes(service.DurationMinutes);
        var before = Duration.FromMinutes(service.BufferBeforeMinutes);
        var after = Duration.FromMinutes(service.BufferAfterMinutes);

        var slots = new List<OpenSlot>();
        foreach (var schedule in staff)
        {
            var weekly = schedule.Weekly.ToLookup(r => r.DayOfWeek);
            var exceptions = schedule.Exceptions.ToLookup(e => e.Date);
            for (var date = plan.From; date <= plan.To; date = date.AddDays(1))
            {
                var localDate = LocalDate.FromDateOnly(date);
                foreach (var (blockStart, blockEnd) in AvailabilityFit.BlocksOn(date, weekly, exceptions))
                {
                    var endLocal = localDate + LocalTime.FromTimeOnly(blockEnd);
                    var endInstant = AvailabilityFit.ResolveBlockEnd(zone.MapLocal(endLocal));
                    for (var local = localDate + LocalTime.FromTimeOnly(blockStart); local < endLocal; local = local.PlusMinutes(settings.SlotStepMinutes))
                    {
                        var mapping = zone.MapLocal(local);
                        if (mapping.Count == 0) continue; // spring-forward gap (R2)
                        var start = mapping.First().ToInstant(); // first occurrence of a fall-back overlap (R2)
                        var end = start + duration;
                        if (end > endInstant || start < earliest || start > latest) continue;

                        var blocked = new UtcInterval((start - before).ToDateTimeUtc(), (end + after).ToDateTimeUtc());
                        if (schedule.Blocked.Any(b => b.Overlaps(blocked))) continue;
                        slots.Add(new OpenSlot(schedule.StaffId, start.ToDateTimeUtc(), end.ToDateTimeUtc()));
                    }
                }
            }
        }
        return slots.OrderBy(s => s.StartUtc).ThenBy(s => s.StaffId.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Null when no date in [from, to] can hold an offered slot (in the past or beyond the advance
    /// window: not an error, ADR 0005). Dates are clipped before any local time is resolved, so far-off
    /// dates never reach date arithmetic.
    /// </summary>
    public static SlotPlan? Plan(
        DateTimeZone zone, SlotSettings settings, SlotService service, DateOnly from, DateOnly to, DateTime nowUtc, bool ignoreMinimumNotice)
    {
        var (earliest, latest) = StartBounds(settings, nowUtc, ignoreMinimumNotice);
        // A start instant has exactly one local date, so offered starts fall on these dates.
        var first = Max(from, earliest.InZone(zone).Date.ToDateOnly());
        var last = Min(to, latest.InZone(zone).Date.ToDateOnly());
        if (first > last) return null;

        // Slots start on or after the first date's start and end by the day after the last date.
        var windowStart = LocalDate.FromDateOnly(first).AtStartOfDayInZone(zone).ToInstant() - Duration.FromMinutes(service.BufferBeforeMinutes);
        var windowEnd = LocalDate.FromDateOnly(last).PlusDays(1).AtStartOfDayInZone(zone).ToInstant() + Duration.FromMinutes(service.BufferAfterMinutes);
        return new SlotPlan(first, last, new UtcInterval(windowStart.ToDateTimeUtc(), windowEnd.ToDateTimeUtc()));
    }

    /// <summary>The earliest and latest offered start, both inclusive (R3, R8).</summary>
    private static (Instant Earliest, Instant Latest) StartBounds(SlotSettings settings, DateTime nowUtc, bool ignoreMinimumNotice)
    {
        var now = Instant.FromDateTimeUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        var earliest = ignoreMinimumNotice ? now : now + Duration.FromMinutes(settings.MinNoticeMinutes);
        return (earliest, now + Duration.FromDays(settings.MaxAdvanceDays)); // N × 24 h of elapsed time
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
