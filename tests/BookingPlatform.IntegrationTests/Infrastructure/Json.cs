using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace BookingPlatform.IntegrationTests.Infrastructure;

public static class Json
{
    public static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body);

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    /// <summary>Asserts an Error response with this status and code, and returns its body.</summary>
    public static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        var body = await ReadAsync(response, expected);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    public static IEnumerable<string> FieldNames(JsonElement error) =>
        error.GetProperty("details").GetProperty("fields").EnumerateObject().Select(p => p.Name);
}
