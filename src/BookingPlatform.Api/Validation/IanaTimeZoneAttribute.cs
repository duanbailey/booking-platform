using System.ComponentModel.DataAnnotations;

namespace BookingPlatform.Api.Validation;

/// <summary>
/// An IANA time zone name in its canonical spelling, e.g. Europe/London (R2, IanaTimeZone in
/// specs/04-api.openapi.yaml). Windows names (GMT Standard Time) and other spellings
/// (europe/london) are refused. Null passes; combine with [Required] where needed.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class IanaTimeZoneAttribute() : ValidationAttribute("Must be an IANA time zone name, e.g. Europe/London.")
{
    public override bool IsValid(object? value) => value is null || (value is string name && IsIanaName(name));

    public static bool IsIanaName(string name) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(name, out var zone)
        && zone.HasIanaId
        && string.Equals(zone.Id, name, StringComparison.Ordinal);
}
