using BookingPlatform.Api.Data;
using NodaTime;
using NodaTime.TimeZones;

namespace BookingPlatform.Api.Slots;

/// <summary>
/// Which availability blocks govern a date, how their edges resolve to instants, and whether a booking still
/// fits them (R8, R2). Slot generation (T13) and the R6 check on availability changes (T19) share these, so a
/// booking fits exactly when a slot with its times could exist in one block, steps aside (decided 2026-11-23).
/// </summary>
public static class AvailabilityFit
{
    /// <summary>The business-local date of an instant: a booking belongs to the date its start falls on.</summary>
    public static DateOnly LocalDate(DateTimeZone zone, DateTime utc) =>
        Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).InZone(zone).Date.ToDateOnly();

    /// <summary>
    /// R8: whether the appointment [start, end) lies inside a single block of <paramref name="date"/>. Buffers
    /// don't count (availability bounds the appointment only), touching blocks stay separate (ADR 0011), and
    /// steps don't count (decided 2026-11-23).
    /// </summary>
    public static bool Fits(DateTimeZone zone, DateOnly date, IEnumerable<(TimeOnly Start, TimeOnly End)> blocks, DateTime startUtc, DateTime endUtc)
    {
        var start = Instant.FromDateTimeUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc));
        var end = Instant.FromDateTimeUtc(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc));
        var localDate = NodaTime.LocalDate.FromDateOnly(date);
        return blocks.Any(b =>
            start >= ResolveBlockStart(zone.MapLocal(localDate + LocalTime.FromTimeOnly(b.Start)))
            && end <= ResolveBlockEnd(zone.MapLocal(localDate + LocalTime.FromTimeOnly(b.End))));
    }

    /// <summary>R8: a date's exceptions if it has any (a row without times means none), else its weekday's weekly blocks.</summary>
    public static IReadOnlyList<(TimeOnly Start, TimeOnly End)> BlocksOn(
        DateOnly date, ILookup<DayOfWeek, AvailabilityRule> weekly, ILookup<DateOnly, AvailabilityException> exceptions)
    {
        var dateExceptions = exceptions[date].ToList();
        if (dateExceptions.Count == 0) return weekly[date.DayOfWeek].Select(r => (r.StartLocal, r.EndLocal)).ToList();
        if (dateExceptions.Any(e => e.StartLocal is null || e.EndLocal is null)) return []; // unavailable all day
        return dateExceptions.Select(e => (e.StartLocal!.Value, e.EndLocal!.Value)).ToList();
    }

    /// <summary>R2: a block start inside a spring-forward gap is the instant the gap ends; an ambiguous one is its earlier occurrence.</summary>
    public static Instant ResolveBlockStart(ZoneLocalMapping mapping) => mapping.Count switch
    {
        0 => mapping.LateInterval.Start,
        _ => mapping.First().ToInstant(),
    };

    /// <summary>R2: a block end inside a gap is the instant the gap ends; an ambiguous one is its later occurrence, so the block covers the whole repeated hour.</summary>
    public static Instant ResolveBlockEnd(ZoneLocalMapping mapping) => mapping.Count switch
    {
        0 => mapping.LateInterval.Start,
        _ => mapping.Last().ToInstant(),
    };
}
