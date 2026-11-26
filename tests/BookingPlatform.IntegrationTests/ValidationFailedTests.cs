using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookingPlatform.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BookingPlatform.IntegrationTests;

/// <summary>
/// T30: clean ValidationFailed details (decided 2026-11-25; the ValidationFailed details in
/// specs/04-api.openapi.yaml). A wrong-typed or unknown field is the only entry, under its own name; a missing
/// required value says "Is required."; JSON that can't be parsed is the single entry body: "Is not valid JSON.";
/// a missing or null body is body: "Is required.".
/// </summary>
public class ValidationFailedTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<HttpClient> ClientAsync() => fixture.Api.ClientFor(await fixture.Sql.InsertUserAsync());

    private static async Task<Dictionary<string, string>> FieldsAsync(HttpResponseMessage response)
    {
        var error = await Json.ErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        return error.GetProperty("details").GetProperty("fields").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? json) =>
        client.SendAsync(new HttpRequestMessage(method, path) { Content = json is null ? null : Json.Body(json) });

    private const string NotValid = "Is not valid.";
    private const string Required = "Is required.";
    private const string NotJson = "Is not valid JSON.";

    // ------------------------------------------------------------ Wrong types: one entry, no spurious "body"

    public static TheoryData<string, string, string, string> WrongTypes => new()
    {
        { "POST", "/v1/auth/login", """{"email":5,"password":"x"}""", "email" },
        { "POST", "/v1/auth/login", """{"email":"a@b.test","password":"x","extra":1}""", "extra" },
        { "PATCH", "/v1/businesses/{guid}/settings", """{"slotStepMinutes":"30"}""", "slotStepMinutes" },
        { "PATCH", "/v1/businesses/{guid}/settings", """{"slotStepMinutes":30.5}""", "slotStepMinutes" },
        { "PATCH", "/v1/me", """{"name":5}""", "name" },
        { "POST", "/v1/bookings", """{"serviceId":"nope","staffId":"00000000-0000-0000-0000-000000000000","startUtc":"2026-12-01T10:00:00Z"}""", "serviceId" },
        { "POST", "/v1/bookings", """{"serviceId":"00000000-0000-0000-0000-000000000000","staffId":"00000000-0000-0000-0000-000000000000","startUtc":"2026-12-01T10:00:00Z","overrideMinimumNotice":"yes"}""", "overrideMinimumNotice" },
        { "POST", "/v1/bookings", """{"serviceId":"00000000-0000-0000-0000-000000000000","staffId":"00000000-0000-0000-0000-000000000000","startUtc":"2026-12-01T10:00:00Z","confirm":{"affectedBookingIds":[5]}}""", "confirm.affectedBookingIds[0]" },
    };

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public async Task A_wrong_typed_or_unknown_field_is_the_only_entry(string method, string path, string json, string field)
    {
        var client = await ClientAsync();
        var fields = await FieldsAsync(await SendAsync(client, new HttpMethod(method), path.Replace("{guid}", Guid.NewGuid().ToString()), json));
        Assert.Equal(new Dictionary<string, string> { [field] = NotValid }, fields);
    }

    /// <summary>
    /// Every action in the API that reads a JSON body, found from the routing table so a new one is covered
    /// too: an unknown field is its only entry, and a body that can't be parsed is the single body entry.
    /// </summary>
    [Fact]
    public async Task Every_endpoint_with_a_body_reports_only_the_offending_field()
    {
        var client = await ClientAsync();
        var actions = fixture.Api.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(a => a.ControllerTypeInfo.Assembly == typeof(Api.Errors.ErrorResponses).Assembly)
            .Where(a => a.Parameters.Any(p => p.BindingInfo?.BindingSource == BindingSource.Body))
            .ToList();
        Assert.True(actions.Count >= 20, $"found only {actions.Count} actions with a body");

        var failures = new List<string>();
        foreach (var action in actions)
        {
            var method = new HttpMethod(action.ActionConstraints!.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
                .Single().HttpMethods.Single());
            var path = "/" + Regex.Replace(action.AttributeRouteInfo!.Template!, @"\{(\w+)[^}]*\}",
                m => m.Groups[1].Value == "date" ? "2026-12-01" : Guid.NewGuid().ToString());

            var unknown = await SendAsync(client, method, path, """{"zzUnknownField":1}""");
            var unknownText = await unknown.Content.ReadAsStringAsync();
            if (unknown.StatusCode != HttpStatusCode.BadRequest || !unknownText.Contains("""{"fields":{"zzUnknownField":"Is not valid."}}"""))
                failures.Add($"{method} {path} unknown field: {(int)unknown.StatusCode} {unknownText}");

            var broken = await SendAsync(client, method, path, """{"zzUnknownField":""");
            var brokenText = await broken.Content.ReadAsStringAsync();
            if (broken.StatusCode != HttpStatusCode.BadRequest || !brokenText.Contains("""{"fields":{"body":"Is not valid JSON."}}"""))
                failures.Add($"{method} {path} broken JSON: {(int)broken.StatusCode} {brokenText}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ------------------------------------------------------------ Required

    [Fact]
    public async Task Missing_required_fields_say_Is_required()
    {
        var client = await ClientAsync();

        Assert.Equal(new Dictionary<string, string> { ["email"] = Required, ["password"] = Required },
            await FieldsAsync(await SendAsync(client, HttpMethod.Post, "/v1/auth/login", "{}")));
        Assert.Equal(new Dictionary<string, string> { ["name"] = Required },
            await FieldsAsync(await SendAsync(client, HttpMethod.Post, "/v1/auth/register",
                """{"email":"a@b.test","password":"long enough password","timeZone":"UTC"}""")));
        Assert.Equal(new Dictionary<string, string> { ["price"] = Required },
            await FieldsAsync(await SendAsync(client, HttpMethod.Post, $"/v1/businesses/{Guid.NewGuid()}/services",
                """{"name":"X","durationMinutes":30,"bufferBeforeMinutes":0,"bufferAfterMinutes":0}""")));
    }

    [Fact]
    public async Task No_validation_message_names_a_CSharp_property()
    {
        // Each required field of these bodies left out at once; every message must be the plain one.
        var client = await ClientAsync();
        var responses = new[]
        {
            await SendAsync(client, HttpMethod.Post, "/v1/auth/register", "{}"),
            await SendAsync(client, HttpMethod.Post, "/v1/bookings", "{}"),
            await SendAsync(client, HttpMethod.Post, $"/v1/businesses/{Guid.NewGuid()}/services", "{}"),
            await SendAsync(client, HttpMethod.Post, $"/v1/staff/{Guid.NewGuid()}/time-off", "{}"),
        };
        foreach (var response in responses)
        {
            var fields = await FieldsAsync(response);
            Assert.NotEmpty(fields);
            Assert.All(fields, f => Assert.Equal(Required, f.Value));
        }
    }

    // ------------------------------------------------------------ Query parameters MVC can't convert

    [Theory]
    [InlineData("/v1/businesses/{guid}/bookings?limit=abc", "limit")]
    [InlineData("/v1/businesses/{guid}/bookings?limit=", "limit")]
    [InlineData("/v1/businesses/{guid}/bookings?limit=99999999999", "limit")]
    [InlineData("/v1/services/{guid}/slots?from=2026-12-01&to=2026-12-02&staffId=nope", "staffId")]
    public async Task A_query_value_that_cannot_be_converted_is_not_valid(string path, string field)
    {
        // MVC's own wording ("The value 'abc' is not valid for Limit.") names the C# property and echoes the input.
        var client = await ClientAsync();
        var fields = await FieldsAsync(await client.GetAsync(path.Replace("{guid}", Guid.NewGuid().ToString())));
        Assert.Equal(new Dictionary<string, string> { [field] = NotValid }, fields);
    }

    // ------------------------------------------------------------ Bodies that aren't JSON objects

    [Theory]
    [InlineData("""{"email":""")]
    [InlineData("""{"email" "a@b.test"}""")]
    [InlineData("""{'email':'a@b.test'}""")]
    [InlineData("""{"email":"a@b.test","password":"x"} trailing""")]
    [InlineData("""{"email":5""")] // a wrong type, but cut off: the syntax error wins
    [InlineData("""{"email":5,"pass""")] // the serializer would stop at the wrong type before the break
    [InlineData("""{"extra":1,""")] // ... or at the unknown field
    [InlineData("""{/* comment */"email":"a@b.test","password":"x"}""")]
    [InlineData("   ")]
    public async Task JSON_that_cannot_be_parsed_is_one_body_entry(string json)
    {
        var client = await ClientAsync();
        Assert.Equal(new Dictionary<string, string> { ["body"] = NotJson },
            await FieldsAsync(await SendAsync(client, HttpMethod.Post, "/v1/auth/login", json)));
    }

    [Fact]
    public async Task A_body_with_a_UTF8_byte_order_mark_is_read_normally()
    {
        var client = await ClientAsync();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("""{"email":5,"password":"x"}""")).ToArray();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var fields = await FieldsAsync(await client.PostAsync("/v1/auth/login", content));

        Assert.Equal(new Dictionary<string, string> { ["email"] = NotValid }, fields); // parsed: the field, not the body
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task A_missing_or_null_body_is_required(string json)
    {
        var client = await ClientAsync();
        Assert.Equal(new Dictionary<string, string> { ["body"] = Required },
            await FieldsAsync(await SendAsync(client, HttpMethod.Post, "/v1/auth/login", json)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    public async Task A_body_that_is_not_an_object_is_one_body_entry(string json)
    {
        var client = await ClientAsync();
        Assert.Equal(new Dictionary<string, string> { ["body"] = NotValid },
            await FieldsAsync(await SendAsync(client, HttpMethod.Post, "/v1/auth/login", json)));
    }
}
