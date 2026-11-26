using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BookingPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// The refresh token cookie (ADR 0013, T32): reading the Set-Cookie the API sends, and sending the cookie back on
/// refresh and logout. Requests go through a client that doesn't handle cookies itself, so each test says exactly
/// which cookie it sends (the test server is plain http, where a cookie container would drop a Secure cookie).
/// </summary>
public static class RefreshCookie
{
    public const string Name = "refresh_token";

    /// <summary>A parsed Set-Cookie header: the value and its attributes, names in lower case.</summary>
    public sealed record SetCookie(string Value, IReadOnlyDictionary<string, string?> Attributes)
    {
        public bool Has(string attribute) => Attributes.ContainsKey(attribute.ToLowerInvariant());

        public string? this[string attribute] => Attributes.GetValueOrDefault(attribute.ToLowerInvariant());
    }

    /// <summary>The response's one Set-Cookie for the refresh token.</summary>
    public static SetCookie Parse(HttpResponseMessage response)
    {
        var headers = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        var header = Assert.Single(headers, h => h.StartsWith(Name + "=", StringComparison.Ordinal));
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var attributes = new Dictionary<string, string?>();
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals < 0) attributes[part.ToLowerInvariant()] = null;
            else attributes[part[..equals].ToLowerInvariant()] = part[(equals + 1)..];
        }
        return new SetCookie(parts[0][(Name.Length + 1)..], attributes);
    }

    /// <summary>The refresh token the response set (asserting it set one).</summary>
    public static string TokenFrom(HttpResponseMessage response)
    {
        var cookie = Parse(response);
        Assert.False(string.IsNullOrEmpty(cookie.Value), "The response cleared the cookie instead of setting it.");
        return cookie.Value;
    }

    public static Task<HttpResponseMessage> RefreshAsync(this ApiFactory api, string? token, HttpContent? body = null) =>
        PostAsync(api, "/v1/auth/refresh", token, body);

    public static Task<HttpResponseMessage> LogoutAsync(this ApiFactory api, string? token, HttpContent? body = null) =>
        PostAsync(api, "/v1/auth/logout", token, body);

    /// <summary>Posts with the cookie header <c>refresh_token=token</c>, or no cookie header when token is null.</summary>
    public static Task<HttpResponseMessage> PostAsync(ApiFactory api, string path, string? token, HttpContent? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        if (token is not null) request.Headers.Add("Cookie", $"{Name}={token}");
        return Client(api).SendAsync(request);
    }

    /// <summary>A client that leaves cookies alone, optionally with an access token.</summary>
    public static HttpClient Client(ApiFactory api, string? accessToken = null)
    {
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        if (accessToken is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}
