using BookingPlatform.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Admin;

/// <summary>
/// Operator script for platform admins (specs/05-data-model.md platform_admins), run as a
/// command of the API binary, e.g. as a one-off ECS task in production:
/// <c>dotnet BookingPlatform.Api.dll admin grant|revoke|list ...</c>. The admin role is never
/// granted or revoked through the API (specs/07-nfr.md).
/// Exit codes: 0 done (or nothing to change), 1 refused, 2 usage error, 3 failed unexpectedly
/// (e.g. the database is unreachable). Each grant and revoke is logged at Warning as an audit
/// line, naming users by ID only (no emails in logs).
/// </summary>
public static class AdminCli
{
    public const int Ok = 0, Refused = 1, UsageError = 2, Failed = 3;

    public const string Usage = """
        Usage:
          admin grant <email>                       Make the first (bootstrap) admin; only while there are no admins.
          admin grant <email> --granted-by <email>  Make a user admin, naming an existing admin as the granter.
          admin revoke <email>                      Remove a user's admin role (never the last admin).
          admin list                                List admins and who granted them.
        Users are named by email and must already have an account.
        """;

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, TextWriter error)
    {
        if (args is not (["grant", _] or ["grant", _, "--granted-by", _] or ["revoke", _] or ["list"]))
        {
            await error.WriteLineAsync(Usage);
            return UsageError;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminCli).FullName!);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

            return args switch
            {
                ["grant", var email] => await GrantAsync(db, logger, email, grantedByEmail: null, now, output, error),
                ["grant", var email, _, var grantedBy] => await GrantAsync(db, logger, email, grantedBy, now, output, error),
                ["revoke", var email] => await RevokeAsync(db, logger, email, output, error),
                _ => await ListAsync(db, output),
            };
        }
        catch (Exception e)
        {
            // A documented exit code instead of an unhandled exception; details go to the log (stderr).
            logger.LogError(e, "Admin command {Command} failed", args[0]);
            await error.WriteLineAsync($"Failed: {e.Message.ReplaceLineEndings(" ")}");
            return Failed;
        }
    }

    private static async Task<int> GrantAsync(BookingDbContext db, ILogger logger,
        string email, string? grantedByEmail, DateTime now, TextWriter output, TextWriter error)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var admins = await LockAdminsAsync(db);

        var user = await FindUserAsync(db, email);
        if (user is null) return await RefuseAsync(error, $"No account has the email {email.Trim()}.");

        Guid? grantedBy = null;
        if (grantedByEmail is null)
        {
            // Only the first (bootstrap) admin has no granter.
            if (admins.Count > 0 && admins.All(a => a.UserId != user.Id))
            {
                return await RefuseAsync(error, "Admins already exist; name one with --granted-by <email>.");
            }
        }
        else
        {
            var granter = await FindUserAsync(db, grantedByEmail);
            if (granter is null) return await RefuseAsync(error, $"No account has the email {grantedByEmail.Trim()}.");
            if (admins.All(a => a.UserId != granter.Id))
            {
                return await RefuseAsync(error, $"{granter.Email} is not an admin, so cannot grant the role.");
            }
            grantedBy = granter.Id;
        }

        if (admins.Any(a => a.UserId == user.Id))
        {
            await output.WriteLineAsync($"{user.Email} is already an admin; nothing changed.");
            return Ok;
        }

        db.PlatformAdmins.Add(new PlatformAdmin { UserId = user.Id, GrantedBy = grantedBy, GrantedAt = now });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        if (grantedBy is null)
            logger.LogWarning("Admin role granted to user {UserId} (bootstrap)", user.Id);
        else
            logger.LogWarning("Admin role granted to user {UserId} by user {GrantedByUserId}", user.Id, grantedBy);
        await output.WriteLineAsync(grantedBy is null
            ? $"{user.Email} is now an admin (bootstrap)."
            : $"{user.Email} is now an admin, granted by {grantedByEmail!.Trim()}.");
        return Ok;
    }

    private static async Task<int> RevokeAsync(BookingDbContext db, ILogger logger, string email, TextWriter output, TextWriter error)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var admins = await LockAdminsAsync(db);

        var user = await FindUserAsync(db, email);
        if (user is null) return await RefuseAsync(error, $"No account has the email {email.Trim()}.");

        var row = admins.SingleOrDefault(a => a.UserId == user.Id);
        if (row is null)
        {
            await output.WriteLineAsync($"{user.Email} is not an admin; nothing changed.");
            return Ok;
        }
        if (admins.Count == 1)
        {
            return await RefuseAsync(error, $"{user.Email} is the last admin; grant another admin first.");
        }

        // Admins this user granted keep the role; their granted_by still names this user.
        db.PlatformAdmins.Remove(row);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        logger.LogWarning("Admin role revoked from user {UserId}", user.Id);
        await output.WriteLineAsync($"{user.Email} is no longer an admin.");
        return Ok;
    }

    private static async Task<int> ListAsync(BookingDbContext db, TextWriter output)
    {
        var admins = await (
            from a in db.PlatformAdmins
            join u in db.Users on a.UserId equals u.Id
            join g in db.Users on a.GrantedBy equals (Guid?)g.Id into granters
            from g in granters.DefaultIfEmpty()
            orderby a.GrantedAt, u.Email
            select new { u.Email, a.GrantedAt, GrantedBy = g == null ? null : g.Email })
            .ToListAsync();

        foreach (var a in admins)
        {
            await output.WriteLineAsync($"{a.Email}\tgranted {a.GrantedAt:yyyy-MM-ddTHH:mm:ssZ}\tby {a.GrantedBy ?? "(bootstrap)"}");
        }
        if (admins.Count == 0) await output.WriteLineAsync("No admins.");
        return Ok;
    }

    /// <summary>
    /// Locks every platform_admins row (and, when there are none, the empty table's gap), so
    /// concurrent runs can't both bootstrap or both revoke and leave no admin.
    /// </summary>
    private static Task<List<PlatformAdmin>> LockAdminsAsync(BookingDbContext db) =>
        db.PlatformAdmins.FromSql($"SELECT * FROM platform_admins FOR UPDATE").ToListAsync();

    private static Task<User?> FindUserAsync(BookingDbContext db, string email)
    {
        var trimmed = email.Trim();
        return db.Users.SingleOrDefaultAsync(u => u.Email == trimmed); // case-insensitive collation
    }

    private static async Task<int> RefuseAsync(TextWriter error, string message)
    {
        await error.WriteLineAsync(message);
        return Refused;
    }
}
