using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookingPlatform.Api.Errors;
using Microsoft.AspNetCore.Http;

namespace BookingPlatform.Api.Bookings;

/// <summary>
/// T29: the Idempotency-Key header on POST /v1/bookings (decided 2026-11-25; the IdempotencyKey parameter in
/// specs/04-api.openapi.yaml).
/// </summary>
public static partial class IdempotencyKeys
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>How long a key is remembered after its booking was created; at exactly this age it has expired.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex ValidKey();

    /// <summary>
    /// The request's key, or null if it sent none. An empty key, two keys, or one outside the format is
    /// 400 ValidationFailed on the header.
    /// </summary>
    public static string? Read(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderName, out var values)) return null;
        if (values.Count != 1 || values[0] is not { } key || !ValidKey().IsMatch(key))
        {
            throw ApiException.ValidationFailed(HeaderName, "Must be 1 to 64 characters, each a letter, digit, dot, underscore or hyphen.");
        }
        return key;
    }

    /// <summary>
    /// Lowercase hex SHA-256 of the fields that make two requests the same: serviceId, staffId, startUtc as an
    /// instant, customerEmail trimmed and ignoring case (absent is its own value) and overrideMinimumNotice.
    /// confirm is left out: it only matters before the booking is made.
    /// </summary>
    public static string RequestHash(Guid serviceId, Guid staffId, DateTime startUtc, string? customerEmail, bool overrideMinimumNotice)
    {
        // A JSON array, so no value can run into the next one.
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            serviceId.ToString("D"), staffId.ToString("D"), startUtc.Ticks, // UTC, as UtcTimestampAttribute parses it
            customerEmail?.Trim().ToLowerInvariant(), overrideMinimumNotice,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
