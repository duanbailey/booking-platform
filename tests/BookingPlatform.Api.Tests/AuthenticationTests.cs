using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T3: JWT access tokens (specs/07-nfr.md, ADR 0010): HS256, 15 minutes, the user's
/// ID in sub, no role claims; anything else gets 401 Unauthenticated.
/// </summary>
public class AuthenticationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private AccessTokenIssuer IssuerAt(DateTimeOffset now, string? signingKey = null, string? issuer = null)
    {
        var options = factory.JwtOptions;
        return new AccessTokenIssuer(Options.Create(new JwtOptions
        {
            Issuer = issuer ?? options.Issuer,
            Audience = options.Audience,
            SigningKey = signingKey ?? options.SigningKey,
        }), new FixedTimeProvider(now));
    }

    private async Task<HttpResponseMessage> GetMeWith(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/test/me");
    }

    [Fact]
    public async Task Valid_token_identifies_the_user()
    {
        var userId = Guid.NewGuid();

        var response = await factory.ClientFor(userId).GetAsync("/test/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>();
        Assert.Equal(userId, body!["userId"]);
    }

    [Fact]
    public void Access_token_lasts_15_minutes_and_carries_only_identity_claims()
    {
        var userId = Guid.NewGuid();

        var issued = IssuerAt(Now).Issue(userId);

        Assert.Equal(Now.UtcDateTime.AddMinutes(15), issued.ExpiresAt);
        Assert.Equal(DateTimeKind.Utc, issued.ExpiresAt.Kind);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);
        Assert.Equal("HS256", jwt.Header.Alg);
        Assert.Equal(userId.ToString(), jwt.Subject);
        Assert.Equal(factory.JwtOptions.Issuer, jwt.Issuer);
        Assert.Equal([factory.JwtOptions.Audience], jwt.Audiences);
        // Roles come from the data on every request, never from the token (specs/07-nfr.md).
        // Exactly the claims ADR 0010 lists.
        Assert.Equal(["aud", "exp", "iat", "iss", "sub"], jwt.Claims.Select(c => c.Type).Distinct().Order());
    }

    [Fact]
    public async Task Expired_token_returns_401()
    {
        var token = IssuerAt(DateTimeOffset.UtcNow.AddMinutes(-20)).Issue(Guid.NewGuid()).Token;
        await ErrorAssert.IsErrorAsync(await GetMeWith(token), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Token_signed_with_another_key_returns_401()
    {
        var token = IssuerAt(DateTimeOffset.UtcNow, signingKey: "some-other-signing-key-that-is-32-bytes-or-more")
            .Issue(Guid.NewGuid()).Token;
        await ErrorAssert.IsErrorAsync(await GetMeWith(token), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Token_from_another_issuer_returns_401()
    {
        var token = IssuerAt(DateTimeOffset.UtcNow, issuer: "someone-else").Issue(Guid.NewGuid()).Token;
        await ErrorAssert.IsErrorAsync(await GetMeWith(token), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Tampered_token_returns_401()
    {
        var token = factory.TokenFor(Guid.NewGuid());
        var parts = token.Split('.');
        // Swap in a payload for a different user, keeping the original signature.
        var otherPayload = factory.TokenFor(Guid.NewGuid()).Split('.')[1];
        var tampered = $"{parts[0]}.{otherPayload}.{parts[2]}";

        await ErrorAssert.IsErrorAsync(await GetMeWith(tampered), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Unsigned_token_returns_401()
    {
        var parts = factory.TokenFor(Guid.NewGuid()).Split('.');
        var noneHeader = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        await ErrorAssert.IsErrorAsync(await GetMeWith($"{noneHeader}.{parts[1]}."),
            HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Garbage_token_returns_401()
    {
        await ErrorAssert.IsErrorAsync(await GetMeWith("not-a-jwt"), HttpStatusCode.Unauthorized, "Unauthenticated");
    }

    [Fact]
    public async Task Endpoint_marked_anonymous_needs_no_token()
    {
        var response = await factory.CreateClient().GetAsync("/test/anonymous");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short-key")]
    public void Signing_key_shorter_than_32_bytes_is_rejected_at_startup(string key)
    {
        using var badFactory = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Jwt:SigningKey", key));
        var error = Assert.ThrowsAny<Exception>(() => badFactory.CreateClient());
        Assert.Contains("SigningKey", error.ToString());
    }
}
