using System.Security.Claims;
using BookingPlatform.Api.Errors;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BookingPlatform.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The caller's user ID from the token's sub claim (specs/07-nfr.md).</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw ApiException.Unauthenticated();
}
