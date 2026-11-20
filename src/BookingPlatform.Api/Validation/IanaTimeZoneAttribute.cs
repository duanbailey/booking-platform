using System.ComponentModel.DataAnnotations;
using NodaTime.TimeZones;

namespace BookingPlatform.Api.Validation;

/// <summary>
/// An IANA time zone name, spelled exactly as in the tz database (R2, IanaTimeZone in
/// specs/04-api.openapi.yaml). Canonical names (Europe/London) and their aliases (GB, UTC, Asia/Calcutta)
/// pass; Windows names (GMT Standard Time) and other spellings (europe/london) are refused. Callers store
/// <see cref="Canonicalize"/>, so one zone is never stored under two spellings (decided 2026-11-20). Checked
/// against NodaTime's embedded tzdb, the data slot generation uses, so the answer does not depend on the OS
/// (decided 2026-11-20, T13). Null passes; combine with [Required] where needed.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class IanaTimeZoneAttribute() : ValidationAttribute("Must be an IANA time zone name, e.g. Europe/London.")
{
    public override bool IsValid(object? value) => value is null || (value is string name && IsIanaName(name));

    public static bool IsIanaName(string name) => TryCanonical(name, out _);

    /// <summary>The canonical name for a name that passed validation, e.g. Etc/UTC for UTC.</summary>
    public static string Canonicalize(string name) =>
        TryCanonical(name, out var canonical) ? canonical : throw new ArgumentException($"'{name}' is not a tz database name.", nameof(name));

    // The map holds every tzdb ID, canonical or alias, keyed by its exact (case-sensitive) spelling.
    private static bool TryCanonical(string name, out string canonical) =>
        TzdbDateTimeZoneSource.Default.CanonicalIdMap.TryGetValue(name, out canonical!);
}
