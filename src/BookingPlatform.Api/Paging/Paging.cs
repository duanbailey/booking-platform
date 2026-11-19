using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace BookingPlatform.Api.Paging;

/// <summary>
/// The list response shape (PageBase in specs/04-api.openapi.yaml): one page of items and the
/// cursor for the next page, null on the last page.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// The ?limit=&amp;cursor= query of a list endpoint (decided 2026-11-19): limit 1-200, default
/// 50; the cursor must be one a previous page returned, otherwise 400 ValidationFailed naming
/// the parameter. An empty cursor is not a cursor, so it is refused too.
/// </summary>
public sealed class PageQuery
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    [FromQuery(Name = "limit")]
    [Range(1, MaxLimit, ErrorMessage = "Must be between {1} and {2}.")]
    public int Limit { get; set; } = DefaultLimit;

    [FromQuery(Name = "cursor")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    [PageCursor]
    public string? Cursor { get; set; }

    /// <summary>The decoded cursor, or null for the first page. Valid once model validation passed.</summary>
    public NameIdCursor? After => Cursor is null ? null : NameIdCursor.TryDecode(Cursor);
}

/// <summary>
/// A cursor for lists ordered by a name, then id: the last item's name and id, as base64url
/// JSON. Opaque to clients; only this class reads or writes it.
/// </summary>
public sealed record NameIdCursor(string Name, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Wire(Name, Id), WireOptions));

    public static NameIdCursor? TryDecode(string cursor)
    {
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(Base64Url.DecodeFromChars(cursor), WireOptions);
            return wire is { N: not null, I: not null } ? new NameIdCursor(wire.N, wire.I.Value) : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Wire(string? N, Guid? I);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>Fails a cursor that no page could have returned (only nulls pass, meaning "first page").</summary>
public sealed class PageCursorAttribute() : ValidationAttribute("Is not a cursor from a previous page.")
{
    public override bool IsValid(object? value) => value is null || (value is string s && NameIdCursor.TryDecode(s) is not null);
}

/// <summary>Pages a query that is already ordered by (name, id) and filtered to items after the cursor.</summary>
public static class PageBuilder
{
    /// <summary><paramref name="fetched"/> holds up to limit + 1 items; the extra one only shows that more follow.</summary>
    public static Page<TOut> From<TIn, TOut>(
        IReadOnlyList<TIn> fetched, int limit, Func<TIn, NameIdCursor> keyOf, Func<TIn, TOut> map)
    {
        var items = fetched.Take(limit).ToList();
        var next = fetched.Count > limit ? keyOf(items[^1]).Encode() : null;
        return new Page<TOut>(items.Select(map).ToList(), next);
    }
}
