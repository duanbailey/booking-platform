using System.Net;
using System.Text.Json;

namespace BookingPlatform.Api.Tests.Infrastructure;

public static class ErrorAssert
{
    /// <summary>
    /// Asserts the response is the shared Error shape (specs/04-api.openapi.yaml):
    /// exactly code, message, rule and details, with details an object.
    /// Returns the parsed body for further checks.
    /// </summary>
    public static async Task<JsonElement> IsErrorAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string? rule = null)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["code", "details", "message", "rule"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        if (rule is null)
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("rule").ValueKind);
        }
        else
        {
            Assert.Equal(rule, body.GetProperty("rule").GetString());
        }
        Assert.Equal(JsonValueKind.Object, body.GetProperty("details").ValueKind);
        return body;
    }
}
