using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Paging;
using BookingPlatform.Api.Slots;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BookingPlatform.Api.Controllers;

/// <summary>The Confirm schema: the bookings shown in a previous 409 ConfirmationRequired (R2, R4, R6).</summary>
public sealed record ConfirmRequest([Required, MinLength(1)] List<Guid>? AffectedBookingIds) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AffectedBookingIds is { } ids && ids.Distinct().Count() != ids.Count)
            yield return new ValidationResult("Must not repeat a booking.", [nameof(AffectedBookingIds)]);
    }
}

/// <summary>A half-open local time range [startLocal, endLocal), HH:mm.</summary>
public sealed record LocalTimeRangeRequest([Required, LocalTime] string StartLocal, [Required, LocalTime] string EndLocal);

public sealed record WeeklyBlockRequest(
    [Required] DayOfWeek? DayOfWeek, [Required, LocalTime] string StartLocal, [Required, LocalTime] string EndLocal);

/// <summary>PUT weekly-availability body: the whole week. Blocks on one day must not overlap (R8).</summary>
public sealed record WeeklyAvailabilityRequest([Required] List<WeeklyBlockRequest?>? Blocks, ConfirmRequest? Confirm) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Blocks is null) yield break;
        if (Blocks.Any(b => b is null)) { yield return new ValidationResult("Must not contain null.", [nameof(Blocks)]); yield break; }
        var parsed = Blocks.Select(b => (Day: b!.DayOfWeek, Range: TimeRanges.TryParse(b.StartLocal, b.EndLocal))).ToList();
        if (parsed.Any(p => p.Day is null || p.Range is null)) yield break; // the field attributes report these
        if (parsed.Any(p => p.Range!.Value.End <= p.Range.Value.Start))
            yield return new ValidationResult("Each block must end after it starts.", [nameof(Blocks)]);
        else if (parsed.GroupBy(p => p.Day).Any(day => TimeRanges.Overlap(day.Select(p => p.Range!.Value))))
            yield return new ValidationResult("Blocks on the same day must not overlap.", [nameof(Blocks)]);
    }
}

/// <summary>
/// PUT date-exceptions/{date} body: either {"unavailable": true} or {"ranges": [...]} with at least
/// one range, never both (R8). The setters record which fields the JSON contained.
/// </summary>
public sealed class DateExceptionRequest : IValidatableObject
{
    private bool? _unavailable;
    private List<LocalTimeRangeRequest?>? _ranges;

    public bool? Unavailable { get => _unavailable; set { _unavailable = value; HasUnavailable = true; } }
    public List<LocalTimeRangeRequest?>? Ranges { get => _ranges; set { _ranges = value; HasRanges = true; } }
    public ConfirmRequest? Confirm { get; set; }

    [JsonIgnore] public bool HasUnavailable { get; private set; }
    [JsonIgnore] public bool HasRanges { get; private set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HasUnavailable && HasRanges)
        {
            yield return new ValidationResult("Send either unavailable or ranges, not both.", [nameof(Unavailable)]);
            yield break;
        }
        if (!HasUnavailable && !HasRanges)
        {
            yield return new ValidationResult("Send \"unavailable\": true, or ranges.");
            yield break;
        }
        if (HasUnavailable)
        {
            if (Unavailable != true) yield return new ValidationResult("Must be true; send ranges for different hours.", [nameof(Unavailable)]);
            yield break;
        }
        if (Ranges is null || Ranges.Count == 0) { yield return new ValidationResult("Must have at least one range.", [nameof(Ranges)]); yield break; }
        if (Ranges.Any(r => r is null)) { yield return new ValidationResult("Must not contain null.", [nameof(Ranges)]); yield break; }
        var parsed = Ranges.Select(r => TimeRanges.TryParse(r!.StartLocal, r.EndLocal)).ToList();
        if (parsed.Any(p => p is null)) yield break; // the field attributes report these
        if (parsed.Any(p => p!.Value.End <= p.Value.Start))
            yield return new ValidationResult("Each range must end after it starts.", [nameof(Ranges)]);
        else if (TimeRanges.Overlap(parsed.Select(p => p!.Value)))
            yield return new ValidationResult("Ranges must not overlap.", [nameof(Ranges)]);
    }
}

/// <summary>The ?from=&amp;to= query of the date-exceptions list: inclusive local dates, strictly yyyy-MM-dd.</summary>
public sealed class DateRangeQuery
{
    [FromQuery(Name = "from")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    [LocalDate]
    public string? From { get; set; }

    [FromQuery(Name = "to")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    [LocalDate]
    public string? To { get; set; }

    /// <summary>Valid once model validation passed.</summary>
    public DateOnly? FromDate => LocalDateAttribute.TryParse(From, out var d) ? d : null;

    public DateOnly? ToDate => LocalDateAttribute.TryParse(To, out var d) ? d : null;
}

public sealed record WeeklyBlockResponse(DayOfWeek DayOfWeek, string StartLocal, string EndLocal);

/// <summary>The WeeklyAvailability schema.</summary>
public sealed record WeeklyAvailabilityResponse(IReadOnlyList<WeeklyBlockResponse> Blocks);

public sealed record LocalTimeRangeResponse(string StartLocal, string EndLocal);

/// <summary>The DateException schema.</summary>
public sealed record DateExceptionResponse(DateOnly Date, bool Unavailable, IReadOnlyList<LocalTimeRangeResponse> Ranges);

/// <summary>HH:mm parsing and overlap checks for half-open local time ranges.</summary>
internal static class TimeRanges
{
    public static (TimeOnly Start, TimeOnly End)? TryParse(string? start, string? end) =>
        LocalTimeAttribute.TryParse(start, out var s) && LocalTimeAttribute.TryParse(end, out var e) ? (s, e) : null;

    /// <summary>Half-open: [09:00, 12:00) and [12:00, 17:00) do not overlap.</summary>
    public static bool Overlap(IEnumerable<(TimeOnly Start, TimeOnly End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        return sorted.Zip(sorted.Skip(1)).Any(pair => pair.Second.Start < pair.First.End);
    }

    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// Weekly availability and date exceptions (R8). A change that leaves bookings outside the new availability goes
/// through the confirm pattern and, on confirmation, cancels them with reason AvailabilityChanged (R6, T19). Access (decided 2026-11-19): the owner and the
/// business's active staff may view any staff member's availability there; a staff member sets
/// their own, the owner anyone's, including a removed member's. Anyone else gets 404, and a
/// colleague who may view but not set gets 403 (specs/07-nfr.md). Writes lock the caller's user row FOR SHARE
/// (cancelled_by references it), then the staff row (ADR 0002), so replacements never interleave and the affected
/// bookings are worked out under the locks.
/// </summary>
[ApiController]
[Route("v1/staff/{staffId:guid}")]
public sealed class AvailabilityController(
    BookingDbContext db, StaffAccessGuard guard, BookingScheduler scheduler, BookingCanceller canceller, BusinessZones zones, TimeProvider time)
    : ControllerBase
{
    [HttpGet("weekly-availability")]
    public async Task<WeeklyAvailabilityResponse> GetWeekly(Guid staffId, CancellationToken ct)
    {
        await guard.RequireAsync(User.GetUserId(), staffId, manage: false, ct);
        return await WeeklyAsync(staffId, ct);
    }

    /// <summary>
    /// Replaces the whole week. R6: bookings on dates without a date exception that no longer fit go through the
    /// confirm pattern (decided 2026-11-23).
    /// </summary>
    [HttpPut("weekly-availability")]
    public async Task<WeeklyAvailabilityResponse> SetWeekly(Guid staffId, WeeklyAvailabilityRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        await using var transaction = await BeginLockedAsync(callerId, staffId, ct);

        var week = request.Blocks!.Select(b => (Day: b!.DayOfWeek!.Value, Range: TimeRanges.TryParse(b.StartLocal, b.EndLocal)!.Value)).ToList();
        var weekByDay = week.ToLookup(b => b.Day, b => b.Range);
        // Dates before yesterday can't hold a booking that hasn't started, in any zone.
        var since = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        var exceptionDates = (await db.AvailabilityExceptions.Where(e => e.StaffId == staffId && e.Date >= since)
            .Select(e => e.Date).Distinct().ToListAsync(ct)).ToHashSet();
        await CancelOutsideAsync(callerId, staffId, request.Confirm?.AffectedBookingIds, onlyDate: null,
            date => exceptionDates.Contains(date) ? null : weekByDay[date.DayOfWeek].ToList(), ct);

        await db.AvailabilityRules.Where(r => r.StaffId == staffId).ExecuteDeleteAsync(ct);
        db.AvailabilityRules.AddRange(week.Select(b => new AvailabilityRule
        {
            Id = Guid.NewGuid(), StaffId = staffId, DayOfWeek = b.Day, StartLocal = b.Range.Start, EndLocal = b.Range.End,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return await WeeklyAsync(staffId, ct);
    }

    /// <summary>A page of date exceptions, one item per date, ordered by date; from and to are inclusive.</summary>
    [HttpGet("date-exceptions")]
    public async Task<Page<DateExceptionResponse>> ListExceptions(
        Guid staffId, [FromQuery] DateRangeQuery range, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var (from, to) = (range.FromDate, range.ToDate);
        if (from > to) throw ApiException.ValidationFailed("from", "Must not be after to.");
        await guard.RequireAsync(User.GetUserId(), staffId, manage: false, ct);
        var after = page.AfterDate();

        var rows = db.AvailabilityExceptions.AsNoTracking().Where(e => e.StaffId == staffId);
        if (from is { } f) rows = rows.Where(e => e.Date >= f);
        if (to is { } t) rows = rows.Where(e => e.Date <= t);
        if (after is not null) rows = rows.Where(e => e.Date > after.Date);
        var dates = await rows.Select(e => e.Date).Distinct().OrderBy(d => d).Take(page.Limit + 1).ToListAsync(ct);

        var shown = dates.Take(page.Limit).ToList();
        var byDate = (await db.AvailabilityExceptions.AsNoTracking()
                .Where(e => e.StaffId == staffId && shown.Contains(e.Date)).ToListAsync(ct))
            .ToLookup(e => e.Date);
        return PageBuilder.From(dates, page.Limit, d => new DateCursor(d).Encode(), d => ToResponse(d, byDate[d]));
    }

    /// <summary>
    /// Replaces that date's availability entirely: different hours, or none. R6: bookings on that date that no
    /// longer fit go through the confirm pattern.
    /// </summary>
    [HttpPut("date-exceptions/{date}")]
    public async Task<DateExceptionResponse> SetException(Guid staffId, string date, DateExceptionRequest request, CancellationToken ct)
    {
        var day = ParseDate(date);
        var callerId = User.GetUserId();
        await using var transaction = await BeginLockedAsync(callerId, staffId, ct);

        var rows = request.HasUnavailable
            ? [new AvailabilityException { Id = Guid.NewGuid(), StaffId = staffId, Date = day }] // both times null: unavailable
            : request.Ranges!.Select(r =>
            {
                var range = TimeRanges.TryParse(r!.StartLocal, r.EndLocal)!.Value;
                return new AvailabilityException { Id = Guid.NewGuid(), StaffId = staffId, Date = day, StartLocal = range.Start, EndLocal = range.End };
            }).ToList();
        var ranges = rows.Where(r => r.StartLocal is not null).Select(r => (r.StartLocal!.Value, r.EndLocal!.Value)).ToList();
        await CancelOutsideAsync(callerId, staffId, request.Confirm?.AffectedBookingIds, day, d => d == day ? ranges : null, ct);

        await db.AvailabilityExceptions.Where(e => e.StaffId == staffId && e.Date == day).ExecuteDeleteAsync(ct);
        db.AvailabilityExceptions.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return ToResponse(day, rows);
    }

    /// <summary>
    /// The date goes back to the weekly availability. 404 if it has no exception, before the R6 check. R6: bookings
    /// on that date that don't fit the weekly availability go through the confirm pattern; DELETE has no body, so
    /// the confirmation is the comma-separated confirmAffectedBookingIds query parameter.
    /// </summary>
    [HttpDelete("date-exceptions/{date}")]
    public async Task<IActionResult> DeleteException(Guid staffId, string date, CancellationToken ct)
    {
        var day = ParseDate(date);
        var callerId = User.GetUserId();
        await using var transaction = await BeginLockedAsync(callerId, staffId, ct);
        // After the access check, so an outsider gets 404 whatever the query holds (code review 2026-11-23).
        var confirmed = ParseConfirmQuery();

        // Under the lock: a simultaneous removal of the same date deletes nothing here, so it is 404. A 409 below
        // rolls the delete back.
        var deleted = await db.AvailabilityExceptions.Where(e => e.StaffId == staffId && e.Date == day).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw ApiException.NotFound();
        var blocks = (await db.AvailabilityRules.AsNoTracking().Where(r => r.StaffId == staffId && r.DayOfWeek == day.DayOfWeek)
            .Select(r => new { r.StartLocal, r.EndLocal }).ToListAsync(ct)).Select(r => (r.StartLocal, r.EndLocal)).ToList();
        await CancelOutsideAsync(callerId, staffId, confirmed, day, d => d == day ? blocks : null, ct);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Checks access, then, in a new transaction, locks the caller's user row FOR SHARE and the staff row (ADR 0002;
    /// cancelled_by references the user) and checks access again under the lock (see StaffAccessGuard).
    /// </summary>
    private async Task<IDbContextTransaction> BeginLockedAsync(Guid callerId, Guid staffId, CancellationToken ct)
    {
        await guard.RequireAsync(callerId, staffId, manage: true, ct);
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (await db.LockUsersAsync([(callerId, false)], ct) is not null) throw ApiException.Unauthenticated();
            await guard.LockAndRequireAsync(callerId, staffId, ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// R6 (T19): the staff member's bookings that have not started and no longer fit the availability
    /// <paramref name="blocksOn"/> gives go through the confirm pattern; on confirmation they are cancelled with
    /// reason AvailabilityChanged inside the caller's transaction (the caller's SaveChanges writes the events). The
    /// R4 window doesn't apply: the cancellation is business-initiated.
    /// </summary>
    private async Task CancelOutsideAsync(Guid callerId, Guid staffId, IReadOnlyCollection<Guid>? confirmed, DateOnly? onlyDate,
        Func<DateOnly, IReadOnlyCollection<(TimeOnly Start, TimeOnly End)>?> blocksOn, CancellationToken ct)
    {
        // The zone can't change meanwhile: a time zone change takes every staff lock of the business (R2).
        var business = await (from s in db.Staff join b in db.Businesses on s.BusinessId equals b.Id where s.Id == staffId select b)
            .AsNoTracking().SingleAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var affected = await scheduler.OutsideAvailabilityAsync(staffId, zones.Resolve(business), now, onlyDate, blocksOn, ct);
        ConfirmGate.Require(affected.Select(b => new AffectedBooking(b.Id, b.StaffId, b.StartUtc, b.EndUtc)).ToList(), confirmed, "R6",
            $"This change leaves {affected.Count} booking(s) outside the staff member's availability; confirming cancels them and notifies everyone affected.");
        if (affected.Count == 0) return;

        var role = business.OwnerUserId == callerId ? ActorRole.Owner : ActorRole.Staff;
        await canceller.CancelAsync(affected, business, callerId, role, CancellationReason.AvailabilityChanged, now, ct);
    }

    /// <summary>
    /// The DELETE confirmation: comma-separated booking IDs in one confirmAffectedBookingIds parameter, with the
    /// same rules as the confirm body (at least one, no repeats). Null when absent; 400 naming it otherwise
    /// (decided 2026-11-23).
    /// </summary>
    private List<Guid>? ParseConfirmQuery()
    {
        const string name = "confirmAffectedBookingIds";
        if (!Request.Query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1) throw ApiException.ValidationFailed(name, "Send the booking IDs once, comma-separated.");
        var ids = new List<Guid>();
        foreach (var part in (values[0] ?? "").Split(','))
        {
            // Spaces around an ID are allowed ("id1, id2", code review 2026-11-23); Guid parsing also tolerates them,
            // but the rule shouldn't rest on that.
            if (!Guid.TryParseExact(part.Trim(), "D", out var id))
                throw ApiException.ValidationFailed(name, "Must be booking IDs from the 409 response, comma-separated.");
            ids.Add(id);
        }
        if (ids.Distinct().Count() != ids.Count) throw ApiException.ValidationFailed(name, "Must not repeat a booking.");
        return ids;
    }

    private async Task<WeeklyAvailabilityResponse> WeeklyAsync(Guid staffId, CancellationToken ct)
    {
        var rules = await db.AvailabilityRules.AsNoTracking().Where(r => r.StaffId == staffId).ToListAsync(ct);
        return new WeeklyAvailabilityResponse(rules
            .OrderBy(r => ((int)r.DayOfWeek + 6) % 7) // Monday first
            .ThenBy(r => r.StartLocal)
            .Select(r => new WeeklyBlockResponse(r.DayOfWeek, TimeRanges.Format(r.StartLocal), TimeRanges.Format(r.EndLocal)))
            .ToList());
    }

    private static DateExceptionResponse ToResponse(DateOnly date, IEnumerable<AvailabilityException> rows)
    {
        var list = rows.ToList();
        var unavailable = list.Any(r => r.StartLocal is null);
        return new DateExceptionResponse(date, unavailable, unavailable
            ? []
            : list.OrderBy(r => r.StartLocal).Select(r => new LocalTimeRangeResponse(TimeRanges.Format(r.StartLocal!.Value), TimeRanges.Format(r.EndLocal!.Value))).ToList());
    }

    private static DateOnly ParseDate(string date) =>
        LocalDateAttribute.TryParse(date, out var day) ? day : throw ApiException.ValidationFailed("date", "Must be a date like 2026-12-24.");
}
