using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BookingPlatform.Api.Errors;

/// <summary>The shared Error shape in specs/04-api.openapi.yaml: code, message, rule and details, always all four.</summary>
public sealed record ErrorResponse(ErrorCode Code, string Message, string? Rule, object Details);

public static class ErrorResponses
{
    /// <summary>Serializer settings for every JSON body the API writes and reads.</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        // Enums in the spec are strings; a number (even a defined one) is a validation error.
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        // Request bodies in the spec set additionalProperties: false.
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        // Integers in the spec are JSON numbers; the web defaults would also accept "30" (T8).
        options.NumberHandling = JsonNumberHandling.Strict;
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static Task WriteAsync(HttpContext context, int status, ErrorResponse error)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(error, JsonOptions);
    }

    public static Task WriteAsync(HttpContext context, ApiException exception) =>
        WriteAsync(context, exception.Status, exception.ToResponse());

    /// <summary>The message for every missing required value: a field, a parameter or the body (T30).</summary>
    public const string RequiredMessage = "Is required.";

    /// <summary>The message for a value of the wrong type or one that can't be converted (T30).</summary>
    public const string NotValidMessage = "Is not valid.";

    /// <summary>
    /// Replaces MVC's default validation response with 400 ValidationFailed whose
    /// details.fields maps each invalid field (camelCase, as in the JSON) to its first problem.
    /// T30 (decided 2026-11-25): JSON that can't be parsed is the single entry "body"; a field
    /// of the wrong type is reported alone, without the body parameter's own "required" error.
    /// </summary>
    public static IActionResult ValidationFailed(ActionContext context)
    {
        var errors = context.ModelState
            .Where(e => e.Value is { Errors.Count: > 0 })
            .Select(e => (e.Key, Error: e.Value!.Errors[0]))
            .ToList();
        // With AllowInputFormatterExceptionMessages off, the JSON formatter records the serializer's own exception.
        var bodyUnreadable = errors.Any(e => e.Error.Exception is JsonException);

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (errors.Any(e => e.Error.Exception is MalformedJsonException)) // SyntaxCheckingJsonInputFormatter
        {
            fields["body"] = "Is not valid JSON.";
        }
        else
        {
            foreach (var (key, error) in errors)
            {
                // A body that couldn't be read leaves the body parameter unbound, and MVC then reports the
                // parameter itself as required; the field that broke the body is the only real problem.
                if (bodyUnreadable && IsBodyParameter(key, context)) continue;
                fields.TryAdd(FieldName(key, context), string.IsNullOrWhiteSpace(error.ErrorMessage) ? NotValidMessage : error.ErrorMessage);
            }
        }

        var body = new ErrorResponse(ErrorCode.ValidationFailed, "The request is not valid.", null, new { fields });
        return new ObjectResult(body) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/json" } };
    }

    private static bool IsBodyParameter(string key, ActionContext context) =>
        context.ActionDescriptor.Parameters.Any(p => p.Name == key && p.BindingInfo?.BindingSource == BindingSource.Body);

    /// <summary>
    /// "$.items[0].Start" or "Items[0].Start" becomes "items[0].start". An error about the
    /// body as a whole (empty key, or the body parameter's own name) is "body"; route and
    /// query parameters keep their names.
    /// </summary>
    private static string FieldName(string key, ActionContext context)
    {
        var path = key.StartsWith('$') ? key.TrimStart('$').TrimStart('.') : key;
        if (path.Length == 0 || IsBodyParameter(path, context)) return "body";
        return string.Join('.', path.Split('.').Select(s => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..]));
    }
}
