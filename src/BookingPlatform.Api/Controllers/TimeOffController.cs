using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Paging;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Controllers;

/// <summary>The TimeOff schema.</summary>
public sealed record TimeOffResponse(Guid Id, Guid StaffId, DateTime StartUtc, DateTime EndUtc)
{
    public static TimeOffResponse From(TimeOff t) => new(t.Id, t.StaffId, t.StartUtc, t.EndUtc);
}

/// <summary>POST time-off body: UTC timestamps with Z; endUtc is exclusive and after startUtc.</summary>
public sealed record CreateTimeOffRequest(
    [Required, UtcTimestamp] string StartUtc, [Required, UtcTimestamp] string EndUtc, ConfirmRequest? Confirm) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UtcTimestampAttribute.TryParse(StartUtc, out var start) && UtcTimestampAttribute.TryParse(EndUtc, out var end) && end <= start)
            yield return new ValidationResult("Must be after startUtc.", [nameof(EndUtc)]);
    }
}

/// <summary>The ?from=&amp;to= query of a list filtered by a UTC window (each list states how), UTC with Z.</summary>
public sealed class UtcRangeQuery
{
    [FromQuery(Name = "from")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    [UtcTimestamp]
    public string? From { get; set; }

    [FromQuery(Name = "to")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    [UtcTimestamp]
    public string? To { get; set; }

    /// <summary>Valid once model validation passed.</summary>
    public DateTime? FromUtc => UtcTimestampAttribute.TryParse(From, out var t) ? t : null;

    public DateTime? ToUtc => UtcTimestampAttribute.TryParse(To, out var t) ? t : null;
}

/// <summary>
/// Time off (stories "Staff or owner blocks time off"), without the R6 check for overlapping
/// bookings (T18; until then `confirm` is accepted and ignored). Access is that of
/// <see cref="StaffAccessGuard"/>. Time off may overlap other time off and lie in the past
/// (decided 2026-11-19).
/// </summary>
[ApiController]
[Route("v1")]
public sealed class TimeOffController(BookingDbContext db, StaffAccessGuard guard, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// A page of time off ordered by start, then id. The filter is by overlap (decided 2026-11-19):
    /// time off that ends after from and starts before to, so time off already under way at from is
    /// included. Half-open, like every interval here.
    /// </summary>
    [HttpGet("staff/{staffId:guid}/time-off")]
    public async Task<Page<TimeOffResponse>> List(Guid staffId, [FromQuery] UtcRangeQuery range, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var (from, to) = (range.FromUtc, range.ToUtc);
        if (from > to) throw ApiException.ValidationFailed("from", "Must not be after to.");
        await guard.RequireAsync(User.GetUserId(), staffId, manage: false, ct);
        var after = page.AfterStartId();

        var query = after is null
            ? db.TimeOff.Where(t => t.StaffId == staffId)
            // Keyset on (start, id); the (staff_id, start_utc, id) index serves it.
            : db.TimeOff.FromSql(
                $"SELECT * FROM time_off WHERE staff_id = {staffId} AND (start_utc > {after.Start} OR (start_utc = {after.Start} AND id > {after.Id}))");
        if (from is { } f) query = query.Where(t => t.EndUtc > f);
        if (to is { } u) query = query.Where(t => t.StartUtc < u);
        var fetched = await query.AsNoTracking().OrderBy(t => t.StartUtc).ThenBy(t => t.Id).Take(page.Limit + 1).ToListAsync(ct);

        return PageBuilder.From(fetched, page.Limit, t => new StartIdCursor(t.StartUtc, t.Id).Encode(), TimeOffResponse.From);
    }

    /// <summary>Blocks time off for the staff member, under the staff lock (ADR 0002), as the R6 check will need (T18).</summary>
    [HttpPost("staff/{staffId:guid}/time-off")]
    public async Task<IActionResult> Create(Guid staffId, CreateTimeOffRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        UtcTimestampAttribute.TryParse(request.StartUtc, out var start); // valid: checked by [UtcTimestamp]
        UtcTimestampAttribute.TryParse(request.EndUtc, out var end);

        await guard.RequireAsync(callerId, staffId, manage: true, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await guard.LockAndRequireAsync(callerId, staffId, ct);

        var timeOff = new TimeOff
        {
            Id = Guid.NewGuid(), StaffId = staffId, StartUtc = start, EndUtc = end, CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.TimeOff.Add(timeOff);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return StatusCode(StatusCodes.Status201Created, TimeOffResponse.From(timeOff));
    }

    /// <summary>Removes time off; it can't affect bookings, so there is no confirmation. 404 if it doesn't exist (any more).</summary>
    [HttpDelete("time-off/{timeOffId:guid}")]
    public async Task<IActionResult> Delete(Guid timeOffId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        var staffId = await db.TimeOff.Where(t => t.Id == timeOffId).Select(t => (Guid?)t.StaffId).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound();

        await guard.RequireAsync(callerId, staffId, manage: true, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await guard.LockAndRequireAsync(callerId, staffId, ct);

        // Under the lock: a simultaneous removal of the same entry deletes nothing here, so it is 404.
        var deleted = await db.TimeOff.Where(t => t.Id == timeOffId).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw ApiException.NotFound();
        await transaction.CommitAsync(ct);
        return NoContent();
    }
}
