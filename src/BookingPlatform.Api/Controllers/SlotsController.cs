using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Slots;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace BookingPlatform.Api.Controllers;

/// <summary>The slot query: from and to are inclusive local dates in the business's time zone, strictly yyyy-MM-dd.</summary>
public sealed class SlotQuery
{
    [FromQuery(Name = "from")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    [Required, LocalDate]
    public string? From { get; set; }

    [FromQuery(Name = "to")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    [Required, LocalDate]
    public string? To { get; set; }

    [FromQuery(Name = "staffId")]
    public Guid? StaffId { get; set; }

    [FromQuery(Name = "ignoreMinimumNotice")]
    public bool IgnoreMinimumNotice { get; set; }

    /// <summary>Set once model validation passed. (Validation reads these getters too, so they must not throw.)</summary>
    public DateOnly? FromDate => LocalDateAttribute.TryParse(From, out var d) ? d : null;

    public DateOnly? ToDate => LocalDateAttribute.TryParse(To, out var d) ? d : null;
}

public sealed record SlotsResponse(string BusinessTimeZone, IReadOnlyList<OpenSlot> Slots);

/// <summary>
/// Open slots for a service (story "Customer views open slots for a service and date range"; R8, with
/// R1, R2 and R3; ADR 0005). The rules are in <see cref="SlotGenerator"/>. Any signed-in user may view
/// slots. Decisions 2026-11-20: a staffId that is not a staff row of the service's business is 404; a
/// removed or unassigned member gives no slots. Reads take no locks: a slot shown here can be gone by
/// the time it is booked, and booking re-checks it under the ADR 0002 lock (T14).
/// </summary>
[ApiController]
[Route("v1")]
public sealed class SlotsController(BookingDbContext db, TimeProvider time, ILogger<SlotsController> logger) : ControllerBase
{
    /// <summary>ADR 0005: a slot query covers at most 30 dates.</summary>
    public const int MaxRangeDays = 30;

    /// <summary>
    /// The longest a booking's blocked interval can be (duration plus both buffers, at the service
    /// maxima), so bookings are found by a range scan on (staff_id, status, blocked_start_utc).
    /// </summary>
    private static readonly TimeSpan MaxBlockedSpan =
        TimeSpan.FromMinutes(ServiceLimits.MaxDurationMinutes + 2 * ServiceLimits.MaxBufferMinutes);

    [HttpGet("services/{serviceId:guid}/slots")]
    public async Task<SlotsResponse> List(Guid serviceId, [FromQuery] SlotQuery query, CancellationToken ct)
    {
        var (from, to) = (query.FromDate!.Value, query.ToDate!.Value);
        if (from > to) throw ApiException.ValidationFailed("from", "Must not be after to.");
        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays) throw ApiException.RangeTooLong(MaxRangeDays);

        var callerId = User.GetUserId();
        var row = Access.Found(await (
                from s in db.Services
                join b in db.Businesses on s.BusinessId equals b.Id
                where s.Id == serviceId
                select new { Service = s, Business = b })
            .AsNoTracking().SingleOrDefaultAsync(ct));
        var (service, business) = (row.Service, row.Business);

        if (query.StaffId is { } requested
            && !await db.Staff.AnyAsync(s => s.Id == requested && s.BusinessId == business.Id, ct))
        {
            throw ApiException.NotFound();
        }

        // R3: the owner may ignore the notice for anyone in the business, staff only for their own slots.
        if (query.IgnoreMinimumNotice && business.OwnerUserId != callerId
            && !(query.StaffId is { } own && await db.Staff.AnyAsync(s => s.Id == own && s.UserId == callerId && s.RemovedAt == null, ct)))
        {
            throw ApiException.Forbidden();
        }

        var zone = ResolveZone(business);
        var settings = new SlotSettings(business.MinNoticeMinutes, business.MaxAdvanceDays, business.SlotStepMinutes);
        var slotService = new SlotService(service.DurationMinutes, service.BufferBeforeMinutes, service.BufferAfterMinutes);
        var now = time.GetUtcNow().UtcDateTime;
        var plan = SlotGenerator.Plan(zone, settings, slotService, from, to, now, query.IgnoreMinimumNotice);
        if (plan is null) return new SlotsResponse(business.TimeZone, []);

        // Active staff assigned to the service (removed staff get no slots).
        var staffQuery =
            from link in db.StaffServices
            join s in db.Staff on link.StaffId equals s.Id
            where link.ServiceId == serviceId && s.BusinessId == business.Id && s.RemovedAt == null
            select s.Id;
        if (query.StaffId is { } only) staffQuery = staffQuery.Where(id => id == only);
        var staffIds = await staffQuery.ToListAsync(ct);
        if (staffIds.Count == 0) return new SlotsResponse(business.TimeZone, []);

        var (windowStart, windowEnd) = (plan.BlockedWindow.Start, plan.BlockedWindow.End);
        var bookingsFrom = windowStart - MaxBlockedSpan;
        var weekly = await db.AvailabilityRules.AsNoTracking().Where(r => staffIds.Contains(r.StaffId)).ToListAsync(ct);
        var exceptions = await db.AvailabilityExceptions.AsNoTracking()
            .Where(e => staffIds.Contains(e.StaffId) && e.Date >= plan.From && e.Date <= plan.To).ToListAsync(ct);
        var timeOff = await db.TimeOff.AsNoTracking()
            .Where(t => staffIds.Contains(t.StaffId) && t.StartUtc < windowEnd && t.EndUtc > windowStart)
            .Select(t => new { t.StaffId, t.StartUtc, t.EndUtc }).ToListAsync(ct);
        // R1: only Confirmed bookings block, by the blocked interval stored when booked (ADR 0004).
        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => staffIds.Contains(b.StaffId) && b.Status == BookingStatus.Confirmed
                && b.BlockedStartUtc > bookingsFrom && b.BlockedStartUtc < windowEnd && b.BlockedEndUtc > windowStart)
            .Select(b => new { b.StaffId, StartUtc = b.BlockedStartUtc, EndUtc = b.BlockedEndUtc }).ToListAsync(ct);

        var blocked = timeOff.Concat(bookings).ToLookup(b => b.StaffId, b => new UtcInterval(b.StartUtc, b.EndUtc));
        var weeklyByStaff = weekly.ToLookup(r => r.StaffId);
        var exceptionsByStaff = exceptions.ToLookup(e => e.StaffId);
        var schedules = staffIds.Select(id => new StaffSchedule(
            id, weeklyByStaff[id].ToList(), exceptionsByStaff[id].ToList(), blocked[id].ToList()));

        return new SlotsResponse(business.TimeZone,
            SlotGenerator.Generate(zone, settings, slotService, from, to, now, query.IgnoreMinimumNotice, schedules));
    }

    /// <summary>
    /// See <see cref="ZoneResolver"/>. A zone neither tzdb nor the OS knows is bad data, never the caller's
    /// fault, so it is a 500 (InternalError) with the business and zone in the log.
    /// </summary>
    private DateTimeZone ResolveZone(Business business)
    {
        var zone = ZoneResolver.Find(business.TimeZone)
            ?? throw new InvalidOperationException(
                $"Business {business.Id} has time zone '{business.TimeZone}', which neither NodaTime's tzdb nor the OS zone data knows.");
        if (!ZoneResolver.IsFromTzdb(zone))
        {
            logger.LogWarning("Business {BusinessId} has time zone {TimeZone}, which NodaTime's tzdb {TzdbVersion} lacks; using the OS zone data. Update NodaTime.",
                business.Id, business.TimeZone, DateTimeZoneProviders.Tzdb.VersionId);
        }
        return zone;
    }
}
