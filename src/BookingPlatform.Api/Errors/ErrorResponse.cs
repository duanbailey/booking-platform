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

    /// <summary>
    /// Replaces MVC's default validation response with 400 ValidationFailed whose
    /// details.fields maps each invalid field (camelCase, as in the JSON) to its first problem.
    /// </summary>
    public static IActionResult ValidationFailed(ActionContext context)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in context.ModelState)
        {
            var error = entry.Errors.FirstOrDefault();
            if (error is null) continue;
            var name = FieldName(key, context);
            fields.TryAdd(name, string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Is not valid." : error.ErrorMessage);
        }

        var body = new ErrorResponse(ErrorCode.ValidationFailed, "The request is not valid.", null, new { fields });
        return new ObjectResult(body) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/json" } };
    }

    /// <summary>
    /// "$.items[0].Start" or "Items[0].Start" becomes "items[0].start". An error about the
    /// body as a whole (empty key, or the body parameter's own name) is "body"; route and
    /// query parameters keep their names.
    /// </summary>
    private static string FieldName(string key, ActionContext context)
    {
        var path = key.StartsWith('$') ? key.TrimStart('$').TrimStart('.') : key;
        var isBodyParameter = context.ActionDescriptor.Parameters.Any(p =>
            p.Name == path && p.BindingInfo?.BindingSource == BindingSource.Body);
        if (path.Length == 0 || isBodyParameter) return "body";
        return string.Join('.', path.Split('.').Select(s => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..]));
    }
}
