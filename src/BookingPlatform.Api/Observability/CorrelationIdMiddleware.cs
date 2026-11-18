using System.Text.RegularExpressions;

namespace BookingPlatform.Api.Observability;

/// <summary>
/// Correlation IDs (specs/07-nfr.md): reuses the client's X-Correlation-Id when it is
/// 1-64 characters of A-Z a-z 0-9 - _ ., otherwise generates one. The ID is returned
/// in the response header, attached to every log line for the request, and readable
/// with <see cref="CorrelationIdExtensions.GetCorrelationId"/> (e.g. for an event's
/// correlationId, specs/06-events.md).
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    internal const string ItemKey = "CorrelationId";

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex ValidId();

    /// <summary>True for 1-64 characters of A-Z a-z 0-9 - _ . (specs/07-nfr.md).</summary>
    public static bool IsValidId(string id) => ValidId().IsMatch(id);

    public async Task InvokeAsync(HttpContext context)
    {
        var sent = context.Request.Headers[HeaderName];
        var id = sent.Count == 1 && IsValidId(sent[0]!) ? sent[0]! : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        context.TraceIdentifier = id;
        context.Response.Headers[HeaderName] = id;

        using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }
}

public static class CorrelationIdExtensions
{
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var id) && id is string s ? s : context.TraceIdentifier;
}
