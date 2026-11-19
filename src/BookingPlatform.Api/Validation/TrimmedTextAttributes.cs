using System.ComponentModel.DataAnnotations;

namespace BookingPlatform.Api.Validation;

/// <summary>
/// An email address, checked after trimming surrounding whitespace (the API stores it trimmed):
/// 1-254 characters, exactly one @ with something on each side, and no whitespace inside.
/// Deliberately loose otherwise; delivery is what proves an address. Null passes.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class EmailAttribute() : ValidationAttribute("Must be an email address of at most 254 characters.")
{
    public const int MaxLength = 254;

    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        if (value is not string text) return false;
        var email = text.Trim();
        var at = email.IndexOf('@');
        return email.Length is > 0 and <= MaxLength
            && at > 0 && at < email.Length - 1 && at == email.LastIndexOf('@')
            && !email.Any(char.IsWhiteSpace);
    }
}

/// <summary>
/// A person's name, checked after trimming surrounding whitespace (the API stores it trimmed):
/// 1-200 characters, so a name of only spaces is refused. Null passes.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class PersonNameAttribute() : ValidationAttribute("Must be 1-200 characters, not counting surrounding spaces.")
{
    public const int MaxLength = 200;

    public override bool IsValid(object? value) =>
        value is null || (value is string text && text.Trim().Length is > 0 and <= MaxLength);
}

/// <summary>
/// A business name, checked after trimming surrounding whitespace (the API stores it trimmed):
/// 1-200 characters. Null passes.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BusinessNameAttribute() : ValidationAttribute("Must be 1-200 characters, not counting surrounding spaces.")
{
    public const int MaxLength = 200;

    public override bool IsValid(object? value) =>
        value is null || (value is string text && text.Trim().Length is > 0 and <= MaxLength);
}

/// <summary>A service's name, checked after trimming like <see cref="BusinessNameAttribute"/>: 1-200 characters. Null passes.</summary>
public sealed class ServiceNameAttribute() : ValidationAttribute("Must be 1-200 characters, not counting surrounding spaces.")
{
    public const int MaxLength = 200;

    public override bool IsValid(object? value) =>
        value is null || (value is string text && text.Trim().Length is > 0 and <= MaxLength);
}

/// <summary>
/// A service description: at most 2000 characters after trimming (the API stores it trimmed, and
/// a blank one as null; decided 2026-11-19). Null and blank pass.
/// </summary>
public sealed class ServiceDescriptionAttribute() : ValidationAttribute("Must be at most 2000 characters, not counting surrounding spaces.")
{
    public const int MaxLength = 2000;

    public override bool IsValid(object? value) => value is null || (value is string text && text.Trim().Length <= MaxLength);

    /// <summary>The value to store: trimmed, and null when blank.</summary>
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A price string (Price in specs/04-api.openapi.yaml): digits with up to two decimals, no sign
/// or spaces, at most 99999999.99 (the DECIMAL(10,2) column). Null passes.
/// </summary>
public sealed partial class PriceAttribute() : ValidationAttribute("Must be an amount like 45 or 45.50, at most 99999999.99.")
{
    public const decimal Max = 99_999_999.99m;

    public override bool IsValid(object? value) => value is null || (value is string text && TryParse(text, out _));

    public static bool TryParse(string text, out decimal price)
    {
        price = 0;
        return Pattern().IsMatch(text)
            && decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out price)
            && price <= Max;
    }

    /// <summary>Always two decimals, invariant culture: 45 is "45.00" (decided 2026-11-19).</summary>
    public static string Format(decimal price) => price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[0-9]+(\.[0-9]{1,2})?\z")] // \z: no trailing newline
    private static partial System.Text.RegularExpressions.Regex Pattern();
}

/// <summary>
/// A local time of day, 24-hour HH:mm from 00:00 to 23:59 (LocalTime in specs/04-api.openapi.yaml;
/// no 24:00, decided 2026-11-19). Null passes.
/// </summary>
public sealed partial class LocalTimeAttribute() : ValidationAttribute("Must be a time like 09:30 (HH:mm, 00:00-23:59).")
{
    public override bool IsValid(object? value) => value is null || (value is string text && TryParse(text, out _));

    public static bool TryParse(string? text, out TimeOnly time)
    {
        time = default;
        return text is not null && Pattern().IsMatch(text)
            && TimeOnly.TryParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A([01][0-9]|2[0-3]):[0-5][0-9]\z")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}

/// <summary>
/// A local date, strictly yyyy-MM-dd (format: date in specs/04-api.openapi.yaml). ASP.NET's own
/// date binding also accepts "12/24/2026", "Dec 24 2026" or a time of day, so date query and path
/// parameters are bound as strings and parsed here. Null passes; an empty string does not.
/// </summary>
public sealed partial class LocalDateAttribute() : ValidationAttribute("Must be a date like 2026-12-24.")
{
    public override bool IsValid(object? value) => value is null || (value is string text && TryParse(text, out _));

    public static bool TryParse(string? text, out DateOnly date)
    {
        date = default;
        return text is not null && Pattern().IsMatch(text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
