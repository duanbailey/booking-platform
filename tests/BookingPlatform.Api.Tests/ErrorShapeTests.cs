using System.Net;
using System.Text.Json;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T3: every error response uses the shared Error shape (specs/04-api.openapi.yaml,
/// specs/07-nfr.md "Responses" and "Errors").
/// </summary>
public class ErrorShapeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Missing_token_returns_401_Unauthenticated()
    {
        var response = await factory.CreateClient().GetAsync("/test/me");
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Forbidden_action_returns_403_Forbidden()
    {
        var response = await factory.ClientFor(Guid.NewGuid()).GetAsync("/test/forbidden");
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task Resource_the_caller_cannot_view_returns_404_NotFound()
    {
        var response = await factory.ClientFor(Guid.NewGuid()).GetAsync("/test/not-found");
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Unknown_route_returns_404_NotFound()
    {
        var response = await factory.ClientFor(Guid.NewGuid()).GetAsync("/v1/no-such-thing");
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task Business_rule_error_carries_its_rule_ID_and_details()
    {
        var response = await factory.CreateClient().GetAsync("/test/rule-error");
        var body = await ErrorAssert.IsErrorAsync(response, (HttpStatusCode)422, "MinimumNotice", rule: "R3");
        Assert.Equal(120, body.GetProperty("details").GetProperty("minNoticeMinutes").GetInt32());
    }

    [Fact]
    public async Task Unexpected_exception_returns_500_InternalError_without_exception_details()
    {
        var response = await factory.CreateClient().GetAsync("/test/boom");

        var body = await ErrorAssert.IsErrorAsync(response, HttpStatusCode.InternalServerError, "InternalError");
        var text = body.ToString();
        Assert.DoesNotContain("secret detail", text);
        Assert.DoesNotContain("InvalidOperationException", text);
        Assert.Empty(body.GetProperty("details").EnumerateObject());
    }

    [Theory]
    [InlineData(false, HttpStatusCode.InternalServerError, "InternalError")]
    [InlineData(true, HttpStatusCode.Forbidden, "Forbidden")]
    public async Task Error_response_drops_headers_the_endpoint_set_before_failing(
        bool expected, HttpStatusCode status, string code)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "headers-1");

        var response = await client.GetAsync($"/test/boom-after-headers?expected={expected}");

        await ErrorAssert.IsErrorAsync(response, status, code);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Headers.CacheControl);
        Assert.Equal("headers-1", Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
    }

    // ------------------------------------------------------------ Requests refused at the HTTP level

    [Fact]
    public async Task Wrong_content_type_returns_415_RequestRejected()
    {
        var response = await factory.CreateClient().PostAsync("/test/validate",
            new StringContent("""{"email":"ann@example.test","count":3}""", System.Text.Encoding.UTF8, "text/plain"));
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.UnsupportedMediaType, "RequestRejected");
    }

    [Fact]
    public async Task Wrong_method_returns_405_RequestRejected()
    {
        var response = await factory.ClientFor(Guid.NewGuid()).DeleteAsync("/test/me");
        await ErrorAssert.IsErrorAsync(response, HttpStatusCode.MethodNotAllowed, "RequestRejected");
    }

    // Kestrel raises these as BadHttpRequestException; the in-memory test server never does
    // (it doesn't enforce size limits), so the middleware is called directly.
    [Theory]
    [InlineData(413, "RequestRejected")]
    [InlineData(431, "RequestRejected")]
    [InlineData(400, "ValidationFailed")]
    public async Task Request_refused_by_the_server_keeps_its_status_with_the_matching_code(int status, string code)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ErrorHandlingMiddleware(
            _ => throw new BadHttpRequestException("refused", status), NullLogger<ErrorHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = JsonDocument.Parse(context.Response.Body).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unexpected_exception_is_logged_as_an_error_with_the_correlation_id()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "boom-trace-1");

        await client.GetAsync("/test/boom");

        Assert.Contains(factory.Logs.Entries, e =>
            e.Level == LogLevel.Error
            && e.Exception is InvalidOperationException
            && Equals(e.Scope.GetValueOrDefault("CorrelationId"), "boom-trace-1"));
    }
}
