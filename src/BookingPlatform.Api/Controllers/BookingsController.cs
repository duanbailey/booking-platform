using System.ComponentModel.DataAnnotations;
using BookingPlatform.Api.Auth;
using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Data;
using BookingPlatform.Api.Errors;
using BookingPlatform.Api.Events;
using BookingPlatform.Api.Slots;
using BookingPlatform.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Controllers;

/// <summary>POST /v1/bookings body. Without customerEmail the caller books for themselves.</summary>
public sealed record CreateBookingRequest(
    [Required] Guid? ServiceId,
    [Required] Guid? StaffId,
    [Required, UtcTimestamp] string StartUtc,
    [Email] string? CustomerEmail,
    bool OverrideMinimumNotice,
    ConfirmRequest? Confirm);

public sealed record BookingCustomer(Guid UserId, string Name, string Email);

/// <summary>The Booking schema. Times are UTC; clients show both time zones (R2).</summary>
public sealed record BookingResponse(
    Guid Id, Guid BusinessId, string BusinessTimeZone, Guid ServiceId, Guid StaffId, BookingCustomer Customer,
    DateTime StartUtc, DateTime EndUtc, BookingStatus Status, DateTime? CancelledAt, CancellationReason? CancellationReason);

/// <summary>
/// Creating bookings (T14): stories "Customer books a slot", "Staff or owner books on behalf of a customer"
/// and "Owner or staff overrides the minimum notice period"; R1, R3, R7, R8; ADR 0002 and ADR 0004; and the R4
/// rebook check through the confirm pattern (T15). Decisions 2026-11-20 are in plan/tasks.md (T14, T15).
/// </summary>
[ApiController]
[Route("v1")]
public sealed class BookingsController(
    BookingDbContext db, BookingScheduler scheduler, BusinessZones zones, OutboxWriter outbox, TimeProvider time) : ControllerBase
{
    private sealed record Target(Service Service, Business Business, Staff Staff);

    /// <summary>
    /// Books a slot. Checks, in order (decided 2026-11-20): 404 (service; staff not in its business), 403
    /// (customerEmail or the override without the right), 422 CustomerNotFound, 422 SlotUnavailable for a
    /// removed staff member, 422 StaffNotAssignedToService, 422 SlotUnavailable for a start slot generation
    /// would not offer even ignoring notice and bookings (R8), 422 MinimumNotice (R3), 409 SlotConflict (R1, R7),
    /// and last 409 ConfirmationRequired (R4), so the warning only appears for a booking that would be made.
    /// </summary>
    [HttpPost("bookings")]
    public async Task<IActionResult> Create(CreateBookingRequest request, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        var (serviceId, staffId) = (request.ServiceId!.Value, request.StaffId!.Value); // [Required]
        UtcTimestampAttribute.TryParse(request.StartUtc, out var start); // valid: checked by [UtcTimestamp]
        var onBehalf = request.CustomerEmail is not null;

        // Before any lock, so a caller who may not do this never holds up the staff member's schedule.
        var target = await LoadAsync(serviceId, staffId, ct);
        RequirePrivileges(target, callerId, onBehalf, request.OverrideMinimumNotice);
        var customerId = callerId;
        if (onBehalf)
        {
            var email = request.CustomerEmail!.Trim();
            // An account without a password counts (decided 2026-11-20); users.email ignores case.
            customerId = await db.Users.Where(u => u.Email == email).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct)
                ?? throw ApiException.CustomerNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // ADR 0002 lock order: user rows (in id order) before the staff row. The booking and history inserts
        // reference the customer and the caller, and those foreign keys take shared locks on their rows; take
        // them first. The customer's is exclusive, so one customer's bookings run one at a time across staff
        // and the R4 check below sees every booking of theirs (decided 2026-11-20); the caller's is shared.
        foreach (var userId in new[] { customerId, callerId }.Distinct().Order())
        {
            var exists = userId == customerId
                ? await db.LockUserAsync(userId, ct) is not null
                : await db.LockUserSharedAsync(userId, ct);
            if (exists) continue;
            // The account was deleted since it was looked up (or since the token was issued).
            throw userId == callerId ? ApiException.Unauthenticated() : ApiException.CustomerNotFound();
        }
        if (await db.LockStaffAsync(staffId, ct) is null) throw ApiException.NotFound();

        // Everything is read again under the lock. The lock is the first statement on the staff row, and
        // REPEATABLE READ takes its snapshot at the first plain read, so these reads are current.
        target = await LoadAsync(serviceId, staffId, ct);
        RequirePrivileges(target, callerId, onBehalf, request.OverrideMinimumNotice);
        var (service, business, staff) = (target.Service, target.Business, target.Staff);
        // Read now, under its lock, so nothing can fail between the commit and the response.
        var customer = await db.Users.AsNoTracking().Where(u => u.Id == customerId)
            .Select(u => new BookingCustomer(u.Id, u.Name, u.Email)).SingleAsync(ct);
        if (staff.RemovedAt is not null) throw ApiException.SlotUnavailable(); // removed staff get no slots
        if (!await db.StaffServices.AnyAsync(l => l.StaffId == staffId && l.ServiceId == serviceId, ct))
            throw ApiException.StaffNotAssignedToService();

        var now = time.GetUtcNow().UtcDateTime;
        var slot = await scheduler.OfferedSlotAsync(zones.Resolve(business), business, service, staffId, start, now, ct)
            ?? throw ApiException.SlotUnavailable();
        // R3: offered from now + notice inclusive; the override bypasses only this check.
        var insideNotice = start < now.AddMinutes(business.MinNoticeMinutes);
        if (insideNotice && !request.OverrideMinimumNotice) throw ApiException.MinimumNotice(business.MinNoticeMinutes);
        var blocked = BookingScheduler.BlockedInterval(start, service); // ADR 0004: snapshotted now
        if (await scheduler.OverlapsActiveBookingAsync(staffId, blocked, ct))
            throw ApiException.SlotConflict(staffId, slot.StartUtc, slot.EndUtc); // R7: the requested slot only
        // R4: the customer's bookings they can no longer cancel still stand; confirm before booking again.
        var originals = await scheduler.InsideCancellationWindowAsync(customerId, business, now, ct);
        ConfirmGate.Require(originals, request.Confirm?.AffectedBookingIds, "R4", onBehalf
            ? "The customer already has a booking with this business inside the cancellation window. It still stands; confirm to book this slot as well."
            : "You already have a booking with this business that can no longer be cancelled online. It still stands: contact the business to cancel it, or confirm to book this slot as well.");

        // The role the actor acted in (decided 2026-11-20): Owner or Staff when a staff privilege was used
        // (booking on behalf, or the override when it was needed), Owner if they are both; else Customer.
        var role = !onBehalf && !insideNotice ? ActorRole.Customer
            : business.OwnerUserId == callerId ? ActorRole.Owner : ActorRole.Staff;
        var booking = new Booking
        {
            Id = Guid.NewGuid(), StaffId = staffId, ServiceId = serviceId, CustomerId = customerId,
            StartUtc = slot.StartUtc, EndUtc = slot.EndUtc, BlockedStartUtc = blocked.Start, BlockedEndUtc = blocked.End,
            Status = BookingStatus.Confirmed, CreatedAt = now, Version = 1,
        };
        db.Bookings.Add(booking);
        db.BookingHistory.Add(new BookingHistory
        {
            Id = Guid.NewGuid(), BookingId = booking.Id, Type = BookingHistoryType.Created, ActorUserId = callerId, ActorRole = role,
            At = now, StaffId = staffId, StartUtc = booking.StartUtc, EndUtc = booking.EndUtc,
            MinimumNoticeOverridden = insideNotice, // R3: recorded only when the override was used
        });
        outbox.Add(new BookingCreated(booking.Id, booking.Version, business.Id, business.TimeZone, serviceId, customerId,
            new EventSlot(staffId, booking.StartUtc, booking.EndUtc), new EventActor(callerId, role)));
        // R4: the original's staff member and the owner are told the customer holds both (one event per original).
        foreach (var original in originals)
        {
            outbox.Add(new RebookedInsideWindow(business.Id, business.TimeZone, customerId, original.BookingId,
                new EventSlot(original.StaffId, original.StartUtc, original.EndUtc), booking.Id,
                new EventSlot(staffId, booking.StartUtc, booking.EndUtc)));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Created($"/v1/bookings/{booking.Id}", new BookingResponse(
            booking.Id, business.Id, business.TimeZone, serviceId, staffId, customer, booking.StartUtc, booking.EndUtc,
            booking.Status, booking.CancelledAt, booking.CancellationReason));
    }

    /// <summary>The service, its business and the staff member; 404 unless the staff row is in that business.</summary>
    private async Task<Target> LoadAsync(Guid serviceId, Guid staffId, CancellationToken ct)
    {
        var row = await (
                from s in db.Services
                join b in db.Businesses on s.BusinessId equals b.Id
                join st in db.Staff on b.Id equals st.BusinessId
                where s.Id == serviceId && st.Id == staffId
                select new Target(s, b, st))
            .AsNoTracking().SingleOrDefaultAsync(ct);
        return row ?? throw ApiException.NotFound();
    }

    /// <summary>
    /// customerEmail and overrideMinimumNotice are for the business's owner (any staff member) and for an
    /// active staff member booking with themselves; anyone else gets 403.
    /// </summary>
    private static void RequirePrivileges(Target target, Guid callerId, bool onBehalf, bool overrideNotice)
    {
        if (!onBehalf && !overrideNotice) return;
        var isOwner = target.Business.OwnerUserId == callerId;
        var isThisStaffMember = target.Staff.UserId == callerId && target.Staff.RemovedAt is null;
        if (!isOwner && !isThisStaffMember) throw ApiException.Forbidden();
    }
}
