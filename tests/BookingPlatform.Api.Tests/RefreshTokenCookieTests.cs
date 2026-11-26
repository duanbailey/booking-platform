using BookingPlatform.Api.Auth;

namespace BookingPlatform.Api.Tests;

/// <summary>T32: the cookie's Max-Age (ADR 0013), in whole seconds rounded down so it never outlives the token.</summary>
public class RefreshTokenCookieTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(999_999, 0)] // a fraction of a second left rounds down
    [InlineData(1_000_000, 1)]
    [InlineData(2_592_000_999_999, 2_592_000)] // 30 days and a bit
    public void Max_age_is_whole_seconds_rounded_down(long microseconds, long expected)
    {
        var expiresAt = Now.AddTicks(microseconds * 10);

        Assert.Equal(TimeSpan.FromSeconds(expected), RefreshTokenCookie.MaxAge(expiresAt, Now));
    }

    [Fact]
    public void An_expiry_already_passed_is_zero_not_negative()
    {
        Assert.Equal(TimeSpan.Zero, RefreshTokenCookie.MaxAge(Now.AddSeconds(-5), Now));
    }
}
