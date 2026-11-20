using NodaTime;
using NodaTime.TimeZones;

namespace BookingPlatform.Api.Slots;

/// <summary>
/// Resolves a stored IANA time zone name (R2). NodaTime's bundled tzdb comes first, so DST rules are the
/// same on every OS (decided 2026-11-20). Names stored before T13 were validated against the OS zone data
/// instead, so a zone the OS knows but this tzdb release lacks (e.g. one added in newer OS tzdata) falls
/// back to the OS data rather than failing; callers log that, since it means NodaTime needs updating.
/// </summary>
public static class ZoneResolver
{
    /// <summary>Null when neither tzdb nor the OS knows the name.</summary>
    /// <param name="tzdbLookup">Replaces the tzdb lookup in tests.</param>
    public static DateTimeZone? Find(string name, Func<string, DateTimeZone?>? tzdbLookup = null)
    {
        var zone = (tzdbLookup ?? DateTimeZoneProviders.Tzdb.GetZoneOrNull)(name);
        if (zone is not null) return zone;
        return name.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(name, out var info)
            ? BclDateTimeZone.FromTimeZoneInfo(info)
            : null;
    }

    public static bool IsFromTzdb(DateTimeZone zone) => zone is not BclDateTimeZone;
}
