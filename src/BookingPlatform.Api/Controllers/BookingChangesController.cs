using System.ComponentModel.DataAnnotations;
using System.Data;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using BookingPlatform.Api.Slots;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Controllers;

/// <summary>POST reschedule body; staffId defaults to the booking's current staff member.</summary>
public sealed record RescheduleRequest([Required, UtcTimestamp] string StartUtc, Guid? StaffId, bool OverrideMinimumNotice);

/// <summary>
/// Cancelling and rescheduling (T17): story "Customer cancels or reschedules, subject to the cancellation
/// window"; R3, R4, R5, R8; ADR 0002, 0003, 0004. Decisions 2026-11-20 are in plan/tasks.md (T17).
/// <para>
/// Roles. The booking's customer is bound by the R4 window; its (active) staff member and the owner are not.
/// A caller who is both is never blocked, and acts as the customer unless a staff privilege was needed:
/// inside the window, the notice override, or (for the owner) another staff member.
/// </para>
/// <para>
/// Locks (ADR 0002 "Implicit locks on user rows count"): the actor's and the customer's user rows first (the
/// customer's FOR UPDATE for a reschedule, so the R4 rebook check sees every move; FOR SHARE for a cancel).
/// Moving a booking between staff needs the customer's row FOR UPDATE, so once these are held the booking's
/// staff member can't change: it is read then, and its staff row (both, for a move, in id order) is locked.
/// The booking row itself is then read FOR UPDATE.
/// </para>
/// <para>
/// Both run at READ COMMITTED (code review 2026-11-20): every read sees what is committed at that moment.
/// At MySQL's default REPEATABLE READ, reading the staff member before the staff lock would fix the
/// transaction's snapshot there, and later reads (the booking, time off, availability, other bookings)
/// would miss changes committed while waiting for the staff lock.
/// </para>
/// </summary>
[ApiController]
[Route("v1")]
public sealed class BookingChangesController(
    BookingDbContext db, AccessResolver access, BookingScheduler scheduler, BusinessZones zones, OutboxWriter outbox, TimeProvider time)
    : ControllerBase
{
    /// <summary>The booking and what it belongs to, as read under the locks, with the caller's roles.</summary>
    private sealed record Current(Booking Booking, Staff Staff, Business Business, Service Service, bool IsCustomer, bool IsBookingStaff, bool IsOwner)
    {
        public bool Privileged => IsBookingStaff || IsOwner;

        public ActorRole StaffRole => IsOwner ? ActorRole.Owner : ActorRole.Staff;

        /// <summary>R4: inclusive, so exactly N hours before may still be changed.</summary>
        public bool InsideWindow(DateTime now) => Booking.StartUtc < now.AddHours(Business.CancellationWindowHours);
    }

    /// <summary>
    /// Cancels the booking. Checks: 404 unless the caller can view it, 422 BookingCancelled, 422
    /// CancellationWindowPassed for a customer inside the window. Reason CustomerCancelled when the caller acts
    /// as the customer, else BusinessCancelled. Staff and owners may cancel a booking that has started.
    /// </summary>
    [HttpPost("bookings/{bookingId:guid}/cancel")]
    public async Task<BookingResponse> Cancel(Guid bookingId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        var customerId = await BeforeLocksAsync(callerId, bookingId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var current = await LockAndLoadAsync(callerId, bookingId, customerId, customerExclusive: false, targetStaffId: null, ct);
        var booking = current.Booking;
        if (booking.Status == BookingStatus.Cancelled) throw ApiException.BookingCancelled();
        var now = time.GetUtcNow().UtcDateTime;
        var insideWindow = current.InsideWindow(now);
        if (!current.Privileged && insideWindow) throw ApiException.CancellationWindowPassed(current.Business.CancellationWindowHours);

        var asCustomer = current.IsCustomer && !(current.Privileged && insideWindow);
        var reason = asCustomer ? CancellationReason.CustomerCancelled : CancellationReason.BusinessCancelled;
        var role = asCustomer ? ActorRole.Customer : current.StaffRole;
        var version = booking.Version + 1;
        await UpdateAsync(booking, ct, setters => setters
            .SetProperty(b => b.Status, BookingStatus.Cancelled)
            .SetProperty(b => b.CancelledAt, now)
            .SetProperty(b => b.CancelledBy, callerId)
            .SetProperty(b => b.CancellationReason, reason)
            .SetProperty(b => b.Version, version));
        outbox.Add(new BookingCancelled(booking.Id, version, current.Business.Id, current.Business.TimeZone, booking.ServiceId,
            booking.CustomerId, new EventSlot(booking.StaffId, booking.StartUtc, booking.EndUtc), new EventActor(callerId, role), reason));
        await db.SaveChangesAsync(ct);
        var response = await BookingQueries.Responses(db, db.Bookings.AsNoTracking().Where(b => b.Id == bookingId)).SingleAsync(ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    /// <summary>
    /// Moves the booking to a new slot, keeping its ID (R5, ADR 0003). Checks: 404 (can't view; staffId outside
    /// the business), 403 (anyone but the owner changing staff member; the override without the right), 422
    /// BookingCancelled, 422 CancellationWindowPassed (customer), then as booking: removed staff, not assigned,
    /// not offered (R8), MinimumNotice (R3), SlotConflict (R1, ignoring the booking's own slot). Naming the
    /// current staff member and start is a no-op.
    /// </summary>
    [HttpPost("bookings/{bookingId:guid}/reschedule")]
    public async Task<BookingResponse> Reschedule(Guid bookingId, RescheduleRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        UtcTimestampAttribute.TryParse(request.StartUtc, out var start); // valid: checked by [UtcTimestamp]
        var customerId = await BeforeLocksAsync(callerId, bookingId, ct);
        // Before any lock, so a caller who may not do this never holds anyone up.
        var before = await LoadAsync(callerId, bookingId, forUpdate: false, ct);
        await RequireTargetAsync(before, request.StaffId ?? before.Booking.StaffId, request.OverrideMinimumNotice, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var current = await LockAndLoadAsync(callerId, bookingId, customerId, customerExclusive: true, request.StaffId, ct);
        var targetId = request.StaffId ?? current.Booking.StaffId;
        await RequireTargetAsync(current, targetId, request.OverrideMinimumNotice, ct);
        var booking = current.Booking;
        if (booking.Status == BookingStatus.Cancelled) throw ApiException.BookingCancelled();
        var now = time.GetUtcNow().UtcDateTime;
        var insideWindow = current.InsideWindow(now);
        if (!current.Privileged && insideWindow) throw ApiException.CancellationWindowPassed(current.Business.CancellationWindowHours);
        if (targetId == booking.StaffId && start == booking.StartUtc) // nothing would move (decided 2026-11-20)
            return await BookingQueries.Responses(db, db.Bookings.AsNoTracking().Where(b => b.Id == bookingId)).SingleAsync(ct);

        var target = await db.Staff.AsNoTracking().SingleAsync(s => s.Id == targetId, ct);
        if (target.RemovedAt is not null) throw ApiException.SlotUnavailable(); // removed staff get no slots
        if (!await db.StaffServices.AnyAsync(l => l.StaffId == targetId && l.ServiceId == booking.ServiceId, ct))
            throw ApiException.StaffNotAssignedToService();
        var slot = await scheduler.OfferedSlotAsync(zones.Resolve(current.Business), current.Business, current.Service, targetId, start, now, ct)
            ?? throw ApiException.SlotUnavailable();
        var insideNotice = start < now.AddMinutes(current.Business.MinNoticeMinutes);
        if (insideNotice && !request.OverrideMinimumNotice) throw ApiException.MinimumNotice(current.Business.MinNoticeMinutes);
        var blocked = BookingScheduler.BlockedInterval(start, current.Service); // ADR 0004: snapshotted again
        if (await scheduler.OverlapsActiveBookingAsync(targetId, blocked, ct, exceptBookingId: booking.Id))
            throw ApiException.SlotConflict(targetId, slot.StartUtc, slot.EndUtc);

        var privilegeUsed = current.Privileged && (!current.IsCustomer || insideWindow || insideNotice || targetId != booking.StaffId);
        var role = privilegeUsed ? current.StaffRole : ActorRole.Customer;
        var version = booking.Version + 1;
        await UpdateAsync(booking, ct, setters => setters
            .SetProperty(b => b.StaffId, targetId)
            .SetProperty(b => b.StartUtc, slot.StartUtc)
            .SetProperty(b => b.EndUtc, slot.EndUtc)
            .SetProperty(b => b.BlockedStartUtc, blocked.Start)
            .SetProperty(b => b.BlockedEndUtc, blocked.End)
            .SetProperty(b => b.Version, version));
        db.BookingHistory.Add(new BookingHistory
        {
            Id = Guid.NewGuid(), BookingId = booking.Id, Type = BookingHistoryType.Rescheduled, ActorUserId = callerId, ActorRole = role,
            At = now, StaffId = targetId, StartUtc = slot.StartUtc, EndUtc = slot.EndUtc,
            PreviousStaffId = booking.StaffId, PreviousStartUtc = booking.StartUtc, PreviousEndUtc = booking.EndUtc,
            MinimumNoticeOverridden = insideNotice, // R3: recorded only when the override was used
        });
        outbox.Add(new BookingRescheduled(booking.Id, version, current.Business.Id, current.Business.TimeZone, booking.ServiceId,
            booking.CustomerId, new EventSlot(booking.StaffId, booking.StartUtc, booking.EndUtc),
            new EventSlot(targetId, slot.StartUtc, slot.EndUtc), new EventActor(callerId, role)));
        await db.SaveChangesAsync(ct);
        var response = await BookingQueries.Responses(db, db.Bookings.AsNoTracking().Where(b => b.Id == bookingId)).SingleAsync(ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    /// <summary>404 unless the caller can view the booking; returns its customer (which never changes).</summary>
    private async Task<Guid> BeforeLocksAsync(Guid callerId, Guid bookingId, CancellationToken ct)
    {
        var booking = Access.Found(await access.ForBookingAsync(callerId, bookingId, ct));
        Access.Require(canView: booking.CanView, canAct: true);
        return await db.Bookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.CustomerId).SingleAsync(ct);
    }

    /// <summary>
    /// The user rows; then the booking's staff member, which can't change once they are held; then its staff row
    /// and the target's (id order); then the booking FOR UPDATE and the caller's roles. Needs READ COMMITTED.
    /// </summary>
    private async Task<Current> LockAndLoadAsync(
        Guid callerId, Guid bookingId, Guid customerId, bool customerExclusive, Guid? targetStaffId, CancellationToken ct)
    {
        if (await db.LockUsersAsync([(callerId, false), (customerId, customerExclusive)], ct) == callerId) throw ApiException.Unauthenticated();
        var staffId = await db.Bookings.AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.StaffId).SingleAsync(ct);
        foreach (var id in new[] { staffId, targetStaffId ?? staffId }.Distinct().Order())
        {
            await db.LockStaffAsync(id, ct);
        }
        return await LoadAsync(callerId, bookingId, forUpdate: true, ct);
    }

    /// <summary>
    /// The booking (FOR UPDATE when <paramref name="forUpdate"/>) with its staff row, business and service;
    /// 404 unless the caller can view it.
    /// </summary>
    private async Task<Current> LoadAsync(Guid callerId, Guid bookingId, bool forUpdate, CancellationToken ct)
    {
        var bookings = forUpdate ? db.Bookings.FromSql($"SELECT * FROM bookings WHERE id = {bookingId} FOR UPDATE") : db.Bookings;
        var booking = await bookings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookingId, ct) ?? throw ApiException.NotFound();
        var row = await (
                from s in db.Staff
                join biz in db.Businesses on s.BusinessId equals biz.Id
                join svc in db.Services on biz.Id equals svc.BusinessId
                where s.Id == booking.StaffId && svc.Id == booking.ServiceId
                select new { Staff = s, Business = biz, Service = svc })
            .AsNoTracking().SingleAsync(ct);
        var current = new Current(booking, row.Staff, row.Business, row.Service,
            IsCustomer: booking.CustomerId == callerId,
            IsBookingStaff: row.Staff.UserId == callerId && row.Staff.RemovedAt is null,
            IsOwner: row.Business.OwnerUserId == callerId);
        Access.Require(canView: current.IsCustomer || current.Privileged, canAct: true);
        return current;
    }

    /// <summary>
    /// 404 for a staffId outside the business. 403 when anyone but the owner names another staff member
    /// (customers stay with theirs, staff move to their own slots), or the override comes from anyone but the
    /// owner or the booking's staff member.
    /// </summary>
    private async Task RequireTargetAsync(Current current, Guid targetId, bool overrideNotice, CancellationToken ct)
    {
        if (targetId != current.Booking.StaffId
            && !await db.Staff.AnyAsync(s => s.Id == targetId && s.BusinessId == current.Business.Id, ct)) throw ApiException.NotFound();
        if (!current.IsOwner && targetId != current.Booking.StaffId) throw ApiException.Forbidden();
        if (overrideNotice && !current.Privileged) throw ApiException.Forbidden();
    }

    /// <summary>
    /// Updates the booking only at the version read under the locks. The locks already serialize every change
    /// to it, so a mismatch means a bug, not a race the caller can retry.
    /// </summary>
    private async Task UpdateAsync(Booking booking, CancellationToken ct,
        Func<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Booking>, Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Booking>> setters)
    {
        var updated = await db.Bookings.Where(b => b.Id == booking.Id && b.Version == booking.Version).ExecuteUpdateAsync(s => setters(s), ct);
        if (updated != 1) throw new InvalidOperationException($"Booking {booking.Id} changed under its locks (version {booking.Version}).");
    }
}
