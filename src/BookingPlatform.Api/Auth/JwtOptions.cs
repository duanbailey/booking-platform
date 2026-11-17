namespace BookingPlatform.Api.Auth;

/// <summary>Token settings (ADR 0010), bound from the "Jwt" configuration section.</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public const int MinSigningKeyBytes = 32; // HS256 needs a key of at least 256 bits

    public string Issuer { get; set; } = "booking-platform";
    public string Audience { get; set; } = "booking-platform";
    /// <summary>HS256 key; from Secrets Manager in AWS, never committed for production.</summary>
    public string SigningKey { get; set; } = "";
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}
