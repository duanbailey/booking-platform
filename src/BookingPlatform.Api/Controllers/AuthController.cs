using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace BookingPlatform.Api.Controllers;

public sealed record RegisterRequest(
    [Required, PersonName] string Name,
    [Required, Email] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required, IanaTimeZone] string TimeZone);

public sealed record LoginRequest([Required] string Email, [Required] string Password);

public sealed record AcceptInviteRequest(
    [Required(AllowEmptyStrings = false)] string Token,
    [Required, StringLength(128, MinimumLength = 8)] string Password);

public sealed record ForgotPasswordRequest([Required, Email] string Email);

public sealed record ResetPasswordRequest(
    [Required(AllowEmptyStrings = false)] string Token,
    [Required, StringLength(128, MinimumLength = 8)] string Password);

/// <summary>An empty current password is allowed here and is simply wrong (422, decided 2026-11-19).</summary>
public sealed record ChangePasswordRequest(
    [Required(AllowEmptyStrings = true)] string CurrentPassword,
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword);

/// <summary>
/// Sign-up, login, accepting an invite, password recovery and change, refresh and logout
/// (specs/04-api.openapi.yaml, ADR 0010). The refresh token travels only in the refresh_token cookie (ADR 0013):
/// every response that starts or renews a login sets it, refresh and logout read it and take no body. Every action is rate limited per client IP
/// (specs/07-nfr.md); refresh and logout use their own, looser limit, since every logged-in
/// client refreshes every 15 minutes. Every action except change-password is anonymous; each
/// says so itself, because [AllowAnonymous] on the class would override [Authorize].
/// </summary>
[ApiController]
[Route("v1/auth")]
[EnableRateLimiting(RateLimits.Auth)]
public sealed class AuthController(
    BookingDbContext db, Passwords passwords, AuthTokenService tokens, InviteService invites,
    PasswordRecoveryService recovery, TimeProvider time)
    : ControllerBase
{
    private const int MySqlDuplicateKey = 1062;

    /// <summary>Story "Customer can sign up": creates the account and logs the visitor in.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email.Trim(),
            Name = request.Name.Trim(),
            TimeZone = IanaTimeZoneAttribute.Canonicalize(request.TimeZone),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        user.PasswordHash = passwords.Hash(user, request.Password);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is MySqlException { Number: MySqlDuplicateKey })
        {
            // users.email is the only unique column a new row can collide on; the unique
            // index (case-insensitive) also settles two sign-ups racing for one email.
            throw ApiException.EmailTaken();
        }
        var body = await tokens.StartLoginAsync(user.Id, ct);
        await transaction.CommitAsync(ct);

        return LoggedIn(body, StatusCodes.Status201Created);
    }

    /// <summary>
    /// Stories "Owner/Staff/Customer can log in". Every failure is the same 401. The password is
    /// checked, upgraded and the session started under the user row lock, so a concurrent
    /// password change or reset (T7a) either runs first, and this login checks the new password,
    /// or after, and revokes this login's refresh token. An upgraded hash is never written over a
    /// password changed meanwhile.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Find the id without a lock, then lock the row by primary key: a locking read by email
        // would take gap locks for unknown emails and block sign-ups for them.
        var userId = await db.Users.Where(u => u.Email == email) // case-insensitive collation
            .Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        var user = userId is null ? null : await db.LockUserAsync(userId.Value, ct);

        var check = passwords.Check(user, request.Password);
        if (check == PasswordCheck.Invalid) throw ApiException.InvalidCredentials();

        if (check == PasswordCheck.ValidNeedsRehash)
        {
            user!.PasswordHash = passwords.Hash(user, request.Password);
            await db.SaveChangesAsync(ct);
        }
        var body = await tokens.StartLoginAsync(user!.Id, ct);
        await transaction.CommitAsync(ct);
        return LoggedIn(body);
    }

    /// <summary>
    /// Story "Invited user sets their password": sets the password and logs the user in.
    /// Every refusal is the same 400 InviteInvalid.
    /// </summary>
    [HttpPost("accept-invite")]
    [AllowAnonymous]
    public async Task<IActionResult> AcceptInvite(AcceptInviteRequest request, CancellationToken ct) =>
        LoggedIn(await invites.AcceptAsync(request.Token, request.Password, ct));

    /// <summary>
    /// Story "User can recover a forgotten password": always 202 with no body, whether or not
    /// the email belongs to an account or is within the 5-minute cooldown (specs/07-nfr.md).
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await recovery.RequestAsync(request.Email.Trim(), ct);
        return Accepted();
    }

    /// <summary>Sets the password from a recovery link and logs the user in. Every refusal is the same 400 ResetLinkInvalid.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct) =>
        LoggedIn(await recovery.ResetAsync(request.Token, request.Password, ct));

    /// <summary>Story "User can change their password": new tokens for the caller; other logins are revoked.</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct) =>
        LoggedIn(await recovery.ChangeAsync(User.GetUserId(), request.CurrentPassword, request.NewPassword, ct));

    /// <summary>
    /// Rotates the token in the cookie (ADR 0010). Any body is ignored. A refusal (no cookie, or an unknown, expired,
    /// used or revoked token) is 401 InvalidRefreshToken; when the request carried a cookie, the 401 also clears it
    /// (decided 2026-11-26, T32). Never without one: a cross-site form post arrives without the cookie
    /// (SameSite=Strict), and clearing it then would let any site log the user out (T32 review). The error is
    /// written here: the error middleware discards a response's headers, which would drop the clearing cookie.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Refresh)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var token = RefreshTokenCookie.Read(Request);
        if (token is null) throw ApiException.InvalidRefreshToken();
        try
        {
            return LoggedIn(await tokens.RefreshAsync(token, ct));
        }
        catch (ApiException e) when (e.Code == ErrorCode.InvalidRefreshToken)
        {
            RefreshTokenCookie.Clear(Response);
            await ErrorResponses.WriteAsync(HttpContext, e);
            return new EmptyResult();
        }
    }

    /// <summary>
    /// Revokes the login the cookie's token belongs to and clears the cookie; 204 with any cookie or none
    /// (ADR 0010, ADR 0013). With no cookie nothing is cleared, for the same reason as in <see cref="Refresh"/>.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Refresh)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (RefreshTokenCookie.Read(Request) is { } token)
        {
            await tokens.LogoutAsync(token, ct);
            RefreshTokenCookie.Clear(Response);
        }
        return NoContent();
    }

    /// <summary>The AuthToken body, with the refresh token moved into the cookie (ADR 0013).</summary>
    private ObjectResult LoggedIn(AuthTokenResponse body, int status = StatusCodes.Status200OK)
    {
        RefreshTokenCookie.Set(Response, body.RefreshToken, body.RefreshTokenExpiresAt, time.GetUtcNow().UtcDateTime);
        return StatusCode(status, body);
    }
}
