using System.Net;
using System.Text;
using BookingPlatform.Api.Tests.Infrastructure;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T3: malformed requests and failed validation return 400 ValidationFailed with
/// details.fields naming each invalid field (specs/04-api.openapi.yaml).
/// </summary>
public class ValidationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private Task<HttpResponseMessage> PostJson(string json) =>
        factory.CreateClient().PostAsync("/test/validate", new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Valid_request_passes()
    {
        var response = await PostJson("""{"email":"ann@example.test","count":3}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_fields_return_400_ValidationFailed_naming_each_field_in_camel_case()
    {
        var response = await PostJson("""{"email":"not-an-email","count":11}""");

        var body = await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        var fields = body.GetProperty("details").GetProperty("fields");
        Assert.Equal(["count", "email"], fields.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(string.IsNullOrWhiteSpace(fields.GetProperty("email").GetString()));
    }

    [Fact]
    public async Task Missing_required_field_returns_400_ValidationFailed()
    {
        var response = await PostJson("""{"count":3}""");

        var body = await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.True(body.GetProperty("details").GetProperty("fields").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Malformed_json_returns_400_ValidationFailed()
    {
        var response = await PostJson("""{"email":""");
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
    }

    [Fact]
    public async Task Wrong_json_type_returns_400_ValidationFailed_naming_the_field()
    {
        var response = await PostJson("""{"email":"ann@example.test","count":"three"}""");

        var body = await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.True(body.GetProperty("details").GetProperty("fields").TryGetProperty("count", out _));
    }

    [Fact]
    public async Task Unknown_property_returns_400_ValidationFailed()
    {
        // Request bodies in the spec set additionalProperties: false.
        var response = await PostJson("""{"email":"ann@example.test","count":3,"isAdmin":true}""");
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
    }

    [Fact]
    public async Task Invalid_query_parameters_are_named_by_their_own_names()
    {
        var response = await factory.CreateClient().GetAsync("/test/query?from=notadate&limit=99");

        var body = await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        var fields = body.GetProperty("details").GetProperty("fields");
        Assert.Equal(["from", "limit"], fields.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Enum_given_by_name_is_accepted()
    {
        var response = await factory.CreateClient().PostAsync("/test/enum",
            new StringContent("""{"colour":"Green"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("1")]  // a defined value, but enums in the spec are strings
    [InlineData("99")] // not a defined value at all
    public async Task Enum_given_as_a_number_returns_400_ValidationFailed(string number)
    {
        var response = await factory.CreateClient().PostAsync("/test/enum",
            new StringContent($$"""{"colour":{{number}}}""", Encoding.UTF8, "application/json"));

        var body = await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
        Assert.True(body.GetProperty("details").GetProperty("fields").TryGetProperty("colour", out _));
    }

    [Fact]
    public async Task Missing_body_returns_400_ValidationFailed()
    {
        var response = await factory.CreateClient().PostAsync("/test/validate",
            new StringContent("", Encoding.UTF8, "application/json"));
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.BadRequest, "ValidationFailed");
    }
}
