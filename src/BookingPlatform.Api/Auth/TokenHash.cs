using System.Security.Cryptography;
using System.Text;

namespace BookingPlatform.Api.Auth;

/// <summary>
/// How every single-use token is stored: the lowercase hex SHA-256 of its UTF-8 bytes
/// (refresh tokens, ADR 0010; invites and recovery links, which the worker writes with the
/// same function, specs/05-data-model.md). The tokens are 256-bit random values, so a fast
/// unsalted hash is enough.
/// </summary>
public static class TokenHash
{
    public static string Of(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
