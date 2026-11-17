using BookingPlatform.Api.Tests.Infrastructure;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T3: correlation IDs (specs/07-nfr.md). Every response has X-Correlation-Id:
/// the client's value when it is 1-64 characters of A-Z a-z 0-9 - _ ., otherwise a new
/// ID. The ID is attached to every log line written for the request.
/// </summary>
public class CorrelationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Header = "X-Correlation-Id";

    private async Task<string> CorrelationIdFor(string? sent, string path = "/test/anonymous")
    {
        var client = factory.CreateClient();
        if (sent is not null) client.DefaultRequestHeaders.TryAddWithoutValidation(Header, sent);
        var response = await client.GetAsync(path);
        return Assert.Single(response.Headers.GetValues(Header));
    }

    [Fact]
    public async Task Id_is_generated_when_the_client_sends_none()
    {
        var first = await CorrelationIdFor(null);
        var second = await CorrelationIdFor(null);

        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Valid_client_id_is_echoed()
    {
        var id = "client-Trace_1.2-" + new string('x', 47); // 64 characters
        Assert.Equal(id, await CorrelationIdFor(id));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("newéline")]
    public async Task Invalid_client_id_is_replaced(string sent)
    {
        var returned = await CorrelationIdFor(sent);
        Assert.NotEqual(sent, returned);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", returned);
    }

    [Fact]
    public async Task Client_id_longer_than_64_characters_is_replaced()
    {
        var sent = new string('a', 65);
        Assert.NotEqual(sent, await CorrelationIdFor(sent));
    }

    [Fact]
    public async Task Error_responses_carry_the_id_too()
    {
        Assert.Equal("err-1", await CorrelationIdFor("err-1", "/test/me"));     // 401
        Assert.Equal("err-2", await CorrelationIdFor("err-2", "/test/boom"));   // 500
    }

    [Fact]
    public async Task Log_lines_for_the_request_carry_the_id()
    {
        await CorrelationIdFor("log-trace-42");

        Assert.Contains(factory.Logs.Entries, e =>
            e.Message == "Probe endpoint reached" && Equals(e.Scope.GetValueOrDefault("CorrelationId"), "log-trace-42"));
    }
}
