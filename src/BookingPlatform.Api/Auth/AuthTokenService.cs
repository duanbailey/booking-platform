namespace BookingPlatform.Api.Auth;

/// <summary>The AuthToken schema in specs/04-api.openapi.yaml.</summary>
public sealed record AuthTokenResponse(
    string AccessToken, string TokenType, DateTime ExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt);

/// <summary>Builds AuthToken responses from an access token and a refresh token (ADR 0010).</summary>
public sealed class AuthTokenService(AccessTokenIssuer access, RefreshTokenService refresh)
{
    /// <summary>Register, login and accept-invite: a new login session.</summary>
    public async Task<AuthTokenResponse> StartLoginAsync(Guid userId, CancellationToken ct = default) =>
        Response(access.Issue(userId), await refresh.IssueForNewLoginAsync(userId, ct));

    /// <exception cref="Errors.ApiException">InvalidRefreshToken.</exception>
    public async Task<AuthTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var rotated = await refresh.RotateAsync(refreshToken, ct);
        return Response(access.Issue(rotated.UserId), rotated.Token);
    }

    public Task LogoutAsync(string refreshToken, CancellationToken ct = default) => refresh.RevokeLoginAsync(refreshToken, ct);

    private static AuthTokenResponse Response(AccessToken accessToken, IssuedRefreshToken refreshToken) =>
        new(accessToken.Token, "Bearer", accessToken.ExpiresAt, refreshToken.Token, refreshToken.ExpiresAt);
}
