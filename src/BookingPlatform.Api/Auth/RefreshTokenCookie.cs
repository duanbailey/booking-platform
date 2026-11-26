using Microsoft.Net.Http.Headers;

namespace BookingPlatform.Api.Auth;

/// <summary>
/// The refresh token travels only in this cookie, never in a body (ADR 0013, amending ADR 0010):
/// <c>refresh_token=…; HttpOnly; Secure; SameSite=Strict; Path=/v1/auth; Max-Age=…</c>. Decided 2026-11-26
/// (plan/tasks.md, T32): Max-Age in whole seconds rounded down, so the cookie never outlives the token, and no
/// Expires; an empty cookie counts as none; every response that sets or clears it says Cache-Control: no-store.
/// </summary>
public static class RefreshTokenCookie
{
    public const string Name = "refresh_token";

    /// <summary>The browser sends the cookie only to the auth endpoints that read it.</summary>
    public const string Path = "/v1/auth";

    /// <summary>Seconds until the token expires, rounded down; never negative.</summary>
    public static TimeSpan MaxAge(DateTime expiresAt, DateTime now) =>
        expiresAt <= now ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Floor((expiresAt - now).TotalSeconds));

    /// <summary>The token the request's cookie carries, or null when there is none or it is empty.</summary>
    public static string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    public static void Set(HttpResponse response, string token, DateTime expiresAt, DateTime now)
    {
        response.Cookies.Append(Name, token, Options(MaxAge(expiresAt, now)));
        NoStore(response);
    }

    /// <summary>An empty value with Max-Age=0 and the same path, so the browser drops the cookie.</summary>
    public static void Clear(HttpResponse response)
    {
        response.Cookies.Append(Name, "", Options(TimeSpan.Zero));
        NoStore(response);
    }

    private static void NoStore(HttpResponse response) => response.Headers[HeaderNames.CacheControl] = "no-store";

    private static CookieOptions Options(TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = Path,
        MaxAge = maxAge,
    };
}
