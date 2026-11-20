using BookingPlatform.Api.Validation;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// R2: time zone names are checked against NodaTime's tzdb, so every OS gives the same answer (T13).
/// Aliases are accepted and stored under their canonical name, so one zone never has two spellings
/// (decided 2026-11-20 after code review; IanaTimeZone in specs/04-api.openapi.yaml).
/// </summary>
public class IanaTimeZoneAttributeTests
{
    [Theory]
    [InlineData("Europe/London")]
    [InlineData("America/New_York")]
    [InlineData("Pacific/Auckland")]
    [InlineData("Etc/UTC")]
    public void Canonical_names_are_accepted_and_kept(string name)
    {
        Assert.True(IanaTimeZoneAttribute.IsIanaName(name));
        Assert.Equal(name, IanaTimeZoneAttribute.Canonicalize(name));
    }

    [Theory]
    [InlineData("UTC", "Etc/UTC")]
    [InlineData("US/Eastern", "America/New_York")]
    [InlineData("Asia/Calcutta", "Asia/Kolkata")]
    [InlineData("Europe/Kiev", "Europe/Kyiv")]
    [InlineData("GB", "Europe/London")]
    [InlineData("America/Buenos_Aires", "America/Argentina/Buenos_Aires")]
    public void Aliases_are_accepted_and_become_their_canonical_name(string alias, string canonical)
    {
        Assert.True(IanaTimeZoneAttribute.IsIanaName(alias));
        Assert.Equal(canonical, IanaTimeZoneAttribute.Canonicalize(alias));
    }

    [Theory]
    [InlineData("europe/london")]
    [InlineData("EUROPE/LONDON")]
    [InlineData("GMT Standard Time")]
    [InlineData("Eastern Standard Time")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    [InlineData(" Europe/London")]
    public void Other_spellings_and_windows_names_are_refused(string name)
    {
        Assert.False(IanaTimeZoneAttribute.IsIanaName(name));
        Assert.Throws<ArgumentException>(() => IanaTimeZoneAttribute.Canonicalize(name));
    }
}
