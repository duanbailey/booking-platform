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
