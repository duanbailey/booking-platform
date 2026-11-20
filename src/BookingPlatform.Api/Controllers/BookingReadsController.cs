using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Paging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Controllers;

/// <summary>
/// The ?status= filter of the booking lists: Confirmed or Cancelled, exactly as spelled. Bind it to a parameter
/// not named "status": MVC would take that name as a prefix and look for status.status.
/// </summary>
public sealed class BookingStatusQuery
{
    [FromQuery(Name = "status")]
    [RegularExpression("^(Confirmed|Cancelled)$", ErrorMessage = "Must be Confirmed or Cancelled.")]
    public string? Status { get; set; }

    /// <summary>Valid once model validation passed. (Validation reads this getter too, so it must not throw.)</summary>
    public BookingStatus? Parsed => Status switch
    {
        "Confirmed" => BookingStatus.Confirmed,
        "Cancelled" => BookingStatus.Cancelled,
        _ => null,
    };
}

/// <summary>The Slot schema: a staff member and [startUtc, endUtc), without buffers.</summary>
public sealed record BookingSlot(Guid StaffId, DateTime StartUtc, DateTime EndUtc);

/// <summary>A history entry's actor; userId only for the booking's staff member and the owner.</summary>
public sealed record HistoryActor(
    ActorRole Role, string Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? UserId);

/// <summary>The BookingHistoryEntry schema; previousSlot only for Rescheduled.</summary>
public sealed record BookingHistoryEntry(
    BookingHistoryType Type, DateTime At, HistoryActor Actor, bool MinimumNoticeOverridden,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BookingSlot? PreviousSlot, BookingSlot Slot);

/// <summary>
/// Reading bookings (T16): one booking, the caller's own bookings, a business's bookings, and a booking's
/// history (ADR 0003). Access follows the table in specs/07-nfr.md; decisions 2026-11-20 are in
/// plan/tasks.md (T16). Lists are ordered by start, then id, and filter on the start: from inclusive,
/// to exclusive. With a cursor, each list's scope (customer, staff member, business) goes inside the keyset
/// query, so the (customer_id, start_utc) and (staff_id, start_utc) indexes serve it (code review 2026-11-20).
/// </summary>
[ApiController]
[Route("v1")]
public sealed class BookingReadsController(BookingDbContext db, AccessResolver access) : ControllerBase
{
    /// <summary>The booking's customer, its (active) staff member, or the business's owner; otherwise 404.</summary>
    [HttpGet("bookings/{bookingId:guid}")]
    public async Task<BookingResponse> Get(Guid bookingId, CancellationToken ct)
    {
        var booking = Access.Found(await access.ForBookingAsync(User.GetUserId(), bookingId, ct));
        Access.Require(canView: booking.CanView, canAct: true);
        return await Responses(db.Bookings.Where(b => b.Id == bookingId)).SingleAsync(ct);
    }

    /// <summary>Bookings where the caller is the customer, in any business.</summary>
    [HttpGet("me/bookings")]
    public async Task<Page<BookingResponse>> ListMine(
        [FromQuery] BookingStatusQuery statusFilter, [FromQuery] UtcRangeQuery range, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        var query = ListQuery.Parse(statusFilter, range, page);
        var bookings = query.After is { } a
            ? db.Bookings.FromSql(
                $"SELECT * FROM bookings WHERE customer_id = {callerId} AND (start_utc > {a.Start} OR (start_utc = {a.Start} AND id > {a.Id}))")
            : db.Bookings.Where(b => b.CustomerId == callerId);
        return await PageAsync(query.Apply(bookings), page.Limit, ct);
    }

    /// <summary>
    /// The owner sees every booking in the business, optionally one staff member's (a staff row of this
    /// business, removed or not; otherwise 404). Staff see only their own row's bookings; another staffId
    /// is 403. Anyone else, and an unknown business, gets 404 (decided 2026-11-20).
    /// </summary>
    [HttpGet("businesses/{businessId:guid}/bookings")]
    public async Task<Page<BookingResponse>> ListBusiness(Guid businessId, [FromQuery] Guid? staffId,
        [FromQuery] BookingStatusQuery statusFilter, [FromQuery] UtcRangeQuery range, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var query = ListQuery.Parse(statusFilter, range, page); // 400 before any access check
        var roles = Access.Found(await access.ForBusinessAsync(User.GetUserId(), businessId, ct));
        Access.Require(canView: roles.IsOwner || roles.IsStaff, canAct: true);

        Guid? onlyStaff;
        if (roles.IsOwner)
        {
            if (staffId is { } id && !await db.Staff.AnyAsync(s => s.Id == id && s.BusinessId == businessId, ct)) throw ApiException.NotFound();
            onlyStaff = staffId;
        }
        else
        {
            if (staffId is { } id && id != roles.StaffId) throw ApiException.Forbidden();
            onlyStaff = roles.StaffId;
        }

        var bookings = (onlyStaff, query.After) switch
        {
            ({ } only, { } a) => db.Bookings.FromSql(
                $"SELECT * FROM bookings WHERE staff_id = {only} AND (start_utc > {a.Start} OR (start_utc = {a.Start} AND id > {a.Id}))"),
            ({ } only, null) => db.Bookings.Where(b => b.StaffId == only),
            (null, { } a) => db.Bookings.FromSql(
                $"SELECT * FROM bookings WHERE staff_id IN (SELECT id FROM staff WHERE business_id = {businessId}) AND (start_utc > {a.Start} OR (start_utc = {a.Start} AND id > {a.Id}))"),
            (null, null) => db.Bookings.Where(b => db.Staff.Where(s => s.BusinessId == businessId).Select(s => s.Id).Contains(b.StaffId)),
        };
        return await PageAsync(query.Apply(bookings), page.Limit, ct);
    }

    /// <summary>
    /// Creation, reschedules and overrides, oldest first (by at, then id). Visible to whoever can view the
    /// booking; actor.userId only to its staff member and the owner (specs/04-api.openapi.yaml).
    /// </summary>
    [HttpGet("bookings/{bookingId:guid}/history")]
    public async Task<Page<BookingHistoryEntry>> History(Guid bookingId, [FromQuery] PageQuery page, CancellationToken ct)
    {
        var after = page.AfterStartId(StartIdList.BookingHistory);
        var booking = Access.Found(await access.ForBookingAsync(User.GetUserId(), bookingId, ct));
        Access.Require(canView: booking.CanView, canAct: true);
        var showIds = booking.IsBookingStaff || booking.Business.IsOwner;

        var history = after is null
            ? db.BookingHistory.Where(h => h.BookingId == bookingId)
            // Keyset on (at, id); the (booking_id, at, id) index serves it (InnoDB appends id).
            : db.BookingHistory.FromSql(
                $"SELECT * FROM booking_history WHERE booking_id = {bookingId} AND (at > {after.Start} OR (at = {after.Start} AND id > {after.Id}))");
        var fetched = await (
                from h in history.AsNoTracking()
                join u in db.Users on h.ActorUserId equals u.Id
                orderby h.At, h.Id
                select new { h, u.Name })
            .Take(page.Limit + 1).ToListAsync(ct);

        return PageBuilder.From(fetched, page.Limit, r => new StartIdCursor(r.h.At, r.h.Id).Encode(StartIdList.BookingHistory), r => new BookingHistoryEntry(
            r.h.Type, r.h.At, new HistoryActor(r.h.ActorRole, r.Name, showIds ? r.h.ActorUserId : null), r.h.MinimumNoticeOverridden,
            r.h.PreviousStaffId is { } previousStaff ? new BookingSlot(previousStaff, r.h.PreviousStartUtc!.Value, r.h.PreviousEndUtc!.Value) : null,
            new BookingSlot(r.h.StaffId, r.h.StartUtc, r.h.EndUtc)));
    }

    /// <summary>
    /// A booking list's validated query: the cursor and the status and start filters. Built before any access
    /// check, so a bad parameter is 400 first.
    /// </summary>
    private sealed record ListQuery(StartIdCursor? After, BookingStatus? Status, DateTime? From, DateTime? To)
    {
        public static ListQuery Parse(BookingStatusQuery status, UtcRangeQuery range, PageQuery page)
        {
            var (from, to) = (range.FromUtc, range.ToUtc);
            if (from > to) throw ApiException.ValidationFailed("from", "Must not be after to.");
            return new ListQuery(page.AfterStartId(StartIdList.Bookings), status.Parsed, from, to);
        }

        public IQueryable<Booking> Apply(IQueryable<Booking> bookings)
        {
            if (Status is { } s) bookings = bookings.Where(b => b.Status == s);
            if (From is { } f) bookings = bookings.Where(b => b.StartUtc >= f);
            if (To is { } u) bookings = bookings.Where(b => b.StartUtc < u);
            return bookings.AsNoTracking();
        }
    }

    private async Task<Page<BookingResponse>> PageAsync(IQueryable<Booking> bookings, int limit, CancellationToken ct)
    {
        var fetched = await Responses(bookings.OrderBy(b => b.StartUtc).ThenBy(b => b.Id)).Take(limit + 1).ToListAsync(ct);
        return PageBuilder.From(fetched, limit, b => new StartIdCursor(b.StartUtc, b.Id).Encode(StartIdList.Bookings), b => b);
    }

    /// <summary>The Booking schema for each booking, keeping the query's order.</summary>
    private IQueryable<BookingResponse> Responses(IQueryable<Booking> bookings) =>
        from b in bookings
        join s in db.Staff on b.StaffId equals s.Id
        join biz in db.Businesses on s.BusinessId equals biz.Id
        join u in db.Users on b.CustomerId equals u.Id
        select new BookingResponse(b.Id, biz.Id, biz.TimeZone, b.ServiceId, b.StaffId, new BookingCustomer(u.Id, u.Name, u.Email),
            b.StartUtc, b.EndUtc, b.Status, b.CancelledAt, b.CancellationReason);
}
