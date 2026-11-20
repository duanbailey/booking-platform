using BookingPlatform.Api.Data;
using NodaTime;

namespace BookingPlatform.Api.Slots;

/// <summary>
/// A business's time zone for slot generation and booking (R2), via <see cref="ZoneResolver"/>. A zone
/// neither tzdb nor the OS knows is bad data, never the caller's fault, so it is a 500 (InternalError)
/// with the business and zone in the log; an OS fallback is logged as a warning (decided 2026-11-20).
/// </summary>
public sealed class BusinessZones(ILogger<BusinessZones> logger)
{
    public DateTimeZone Resolve(Business business)
    {
        var zone = ZoneResolver.Find(business.TimeZone)
            ?? throw new InvalidOperationException(
                $"Business {business.Id} has time zone '{business.TimeZone}', which neither NodaTime's tzdb nor the OS zone data knows.");
        if (!ZoneResolver.IsFromTzdb(zone))
        {
            logger.LogWarning("Business {BusinessId} has time zone {TimeZone}, which NodaTime's tzdb {TzdbVersion} lacks; using the OS zone data. Update NodaTime.",
                business.Id, business.TimeZone, DateTimeZoneProviders.Tzdb.VersionId);
        }
        return zone;
    }
}
