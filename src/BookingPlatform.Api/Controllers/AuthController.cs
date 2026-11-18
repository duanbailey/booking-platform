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

public sealed record RefreshTokenRequest([Required] string RefreshToken);

/// <summary>
/// Sign-up, login, refresh and logout (specs/04-api.openapi.yaml, ADR 0010). Every action
/// is anonymous and rate limited per client IP (specs/07-nfr.md); refresh and logout use
/// their own, looser limit, since every logged-in client refreshes every 15 minutes.
/// </summary>
[ApiController]
[Route("v1/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimits.Auth)]
public sealed class AuthController(
    BookingDbContext db, Passwords passwords, AuthTokenService tokens, TimeProvider time) : ControllerBase
{
    private const int MySqlDuplicateKey = 1062;

    /// <summary>Story "Customer can sign up": creates the account and logs the visitor in.</summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email.Trim(),
            Name = request.Name.Trim(),
            TimeZone = request.TimeZone,
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

        return StatusCode(StatusCodes.Status201Created, body);
    }

    /// <summary>Stories "Owner/Staff/Customer can log in". Every failure is the same 401.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct); // case-insensitive collation

        var check = passwords.Check(user, request.Password);
        if (check == PasswordCheck.Invalid) throw ApiException.InvalidCredentials();

        if (check == PasswordCheck.ValidNeedsRehash)
        {
            user!.PasswordHash = passwords.Hash(user, request.Password);
            await db.SaveChangesAsync(ct);
        }
        return Ok(await tokens.StartLoginAsync(user!.Id, ct));
    }

    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimits.Refresh)]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken ct) =>
        Ok(await tokens.RefreshAsync(request.RefreshToken, ct));

    /// <summary>Revokes the login the token belongs to; 204 for any token (ADR 0010).</summary>
    [HttpPost("logout")]
    [EnableRateLimiting(RateLimits.Refresh)]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken ct)
    {
        await tokens.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }
}
