using BookingPlatform.Api.Slots;
using NodaTime;
using NodaTime.TimeZones;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// R2: a stored business time zone resolves through NodaTime's tzdb, else through the OS zone data, which
/// is what zone names were validated against before T13 (code review finding, 2026-11-20).
/// </summary>
public class ZoneResolverTests
{
    [Fact]
    public void A_tzdb_name_resolves_through_tzdb()
    {
        var zone = ZoneResolver.Find("America/New_York");

        Assert.NotNull(zone);
        Assert.IsNotType<BclDateTimeZone>(zone);
        Assert.Equal("America/New_York", zone.Id);
    }

    [Fact]
    public void A_name_tzdb_lacks_falls_back_to_the_OS_zone_data()
    {
        var zone = ZoneResolver.Find("America/New_York", tzdbLookup: _ => null);

        Assert.IsType<BclDateTimeZone>(zone);
        var summer = Instant.FromUtc(2027, 7, 1, 12, 0);
        var winter = Instant.FromUtc(2027, 1, 1, 12, 0);
        Assert.Equal(Offset.FromHours(-4), zone.GetUtcOffset(summer));
        Assert.Equal(Offset.FromHours(-5), zone.GetUtcOffset(winter));
    }

    [Fact]
    public void A_name_neither_knows_is_null()
    {
        Assert.Null(ZoneResolver.Find("Mars/Olympus_Mons"));
        Assert.Null(ZoneResolver.Find(""));
    }
}
