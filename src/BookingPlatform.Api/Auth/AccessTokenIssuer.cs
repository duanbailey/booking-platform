using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BookingPlatform.Api.Auth;

public sealed record AccessToken(string Token, DateTime ExpiresAt);

/// <summary>
/// Issues HS256 access tokens with exactly the claims ADR 0010 lists: sub (the user's ID),
/// iat, exp, iss and aud. Roles are never claims; they are resolved from the data on every request.
/// </summary>
public sealed class AccessTokenIssuer
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _time;
    private readonly SigningCredentials _credentials;
    // No automatic nbf (or iat/exp): every time claim is set explicitly below.
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _credentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256);
    }

    /// <summary>The HS256 key, shared with token validation in Program.cs.</summary>
    public static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));

    public AccessToken Issue(Guid userId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)); // JWT times are whole seconds
        var expires = now + _options.AccessTokenLifetime;

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            Expires = expires,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())]),
            SigningCredentials = _credentials,
        });
        return new AccessToken(token, expires);
    }
}
