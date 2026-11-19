using System.Buffers.Text;
using BookingPlatform.Api.Errors;
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
/// 50; the cursor must be one a previous page of the same kind of list returned, otherwise 400
/// ValidationFailed naming the parameter. An empty cursor is not a cursor, so it is refused too.
/// Each list decodes the cursor with the method for its sort key.
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
    public string? Cursor { get; set; }

    /// <summary>For lists ordered by (name, id): the decoded cursor, or null for the first page.</summary>
    /// <exception cref="ApiException">ValidationFailed naming cursor if it is not such a cursor.</exception>
    public NameIdCursor? AfterNameId() => Cursor is null ? null : NameIdCursor.TryDecode(Cursor) ?? throw InvalidCursor();

    /// <summary>For lists ordered by date: the decoded cursor, or null for the first page.</summary>
    /// <exception cref="ApiException">ValidationFailed naming cursor if it is not such a cursor.</exception>
    public DateCursor? AfterDate() => Cursor is null ? null : DateCursor.TryDecode(Cursor) ?? throw InvalidCursor();

    private static ApiException InvalidCursor() => ApiException.ValidationFailed("cursor", "Is not a cursor from a previous page.");
}

/// <summary>
/// Base64url JSON, the encoding every cursor uses. Unknown members are refused, so a cursor from
/// one kind of list is never accepted by another.
/// </summary>
internal static class CursorCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static string Encode<T>(T wire) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(wire, Options));

    public static T? TryDecode<T>(string cursor) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Base64Url.DecodeFromChars(cursor), Options);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>A cursor for lists ordered by date (one item per date): the last item's date.</summary>
public sealed record DateCursor(DateOnly Date)
{
    public string Encode() => CursorCodec.Encode(new Wire(Date));

    public static DateCursor? TryDecode(string cursor) =>
        CursorCodec.TryDecode<Wire>(cursor) is { D: { } date } ? new DateCursor(date) : null;

    private sealed record Wire(DateOnly? D);
}

/// <summary>
/// A cursor for lists ordered by a name, then id: the last item's name and id, as base64url
/// JSON. Opaque to clients; only this class reads or writes it.
/// </summary>
public sealed record NameIdCursor(string Name, Guid Id)
{
    public string Encode() => CursorCodec.Encode(new Wire(Name, Id));

    public static NameIdCursor? TryDecode(string cursor) =>
        CursorCodec.TryDecode<Wire>(cursor) is { N: { } name, I: { } id } ? new NameIdCursor(name, id) : null;

    private sealed record Wire(string? N, Guid? I);
}

/// <summary>Pages a query that is already in list order and filtered to items after the cursor.</summary>
public static class PageBuilder
{
    /// <summary>
    /// <paramref name="fetched"/> holds up to limit + 1 items; the extra one only shows that more
    /// follow. <paramref name="cursorOf"/> encodes the last returned item's sort key.
    /// </summary>
    public static Page<TOut> From<TIn, TOut>(
        IReadOnlyList<TIn> fetched, int limit, Func<TIn, string> cursorOf, Func<TIn, TOut> map)
    {
        var items = fetched.Take(limit).ToList();
        var next = fetched.Count > limit ? cursorOf(items[^1]) : null;
        return new Page<TOut>(items.Select(map).ToList(), next);
    }
}
