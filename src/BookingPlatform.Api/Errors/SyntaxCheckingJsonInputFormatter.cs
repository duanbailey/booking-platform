using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace BookingPlatform.Api.Errors;

/// <summary>
/// T30 (decided 2026-11-25): JSON that can't be parsed is reported as the body, wherever it breaks. The
/// serializer stops at its first problem, so a body such as <c>{"extra":</c> or <c>{"email":5,"pass</c> would
/// otherwise be blamed on a field it met before the break. This formatter reads the body, checks its syntax
/// with the reader, and only then lets the standard formatter deserialize it; a broken body is recorded as a
/// <see cref="MalformedJsonException"/>, which <see cref="ErrorResponses.ValidationFailed"/> turns into the
/// single entry "body".
/// </summary>
public sealed class SyntaxCheckingJsonInputFormatter(JsonOptions options, ILogger<SystemTextJsonInputFormatter> logger)
    : SystemTextJsonInputFormatter(options, logger)
{
    private readonly JsonReaderOptions _readerOptions = new()
    {
        CommentHandling = options.JsonSerializerOptions.ReadCommentHandling,
        AllowTrailingCommas = options.JsonSerializerOptions.AllowTrailingCommas,
        MaxDepth = options.JsonSerializerOptions.MaxDepth,
    };

    public override async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        // Another charset is transcoded by the standard formatter; only UTF-8 bytes can be checked here.
        if (SelectCharacterEncoding(context) is not { CodePage: 65001 }) return await base.ReadAsync(context);

        var request = context.HttpContext.Request;
        var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, context.HttpContext.RequestAborted); // Kestrel's size limit still applies
        buffer.Position = 0;

        if (buffer.Length > 0 && !IsWellFormed(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
        {
            context.ModelState.TryAddModelError(string.Empty, new MalformedJsonException(), context.Metadata);
            return InputFormatterResult.Failure();
        }

        // An empty body goes on too, so the standard formatter reports it as missing.
        var original = request.Body;
        request.Body = buffer;
        try
        {
            return await base.ReadAsync(context);
        }
        finally
        {
            request.Body = original;
        }
    }

    /// <summary>One complete JSON value and nothing after it but whitespace, under the serializer's reader rules.</summary>
    private bool IsWellFormed(ReadOnlySpan<byte> json)
    {
        // The serializer skips a UTF-8 byte order mark; the reader doesn't.
        if (json.StartsWith(Encoding.UTF8.Preamble)) json = json[Encoding.UTF8.Preamble.Length..];
        var reader = new Utf8JsonReader(json, _readerOptions);
        try
        {
            // The whole body is one final block, so the reader itself throws on a value cut off, text after it,
            // or no value at all.
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>The request body is not well-formed JSON (T30). A <see cref="JsonException"/>, as the serializer's own errors are.</summary>
public sealed class MalformedJsonException() : JsonException("The request body is not well-formed JSON.");
