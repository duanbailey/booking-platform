using BookingPlatform.Api.Data;
using BookingPlatform.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace BookingPlatform.Api.Bookings;

/// <summary>
/// Cancels bookings inside the caller's transaction: a customer or the business cancelling one (T17), and the
/// R6 changes that cancel the bookings they affect (time off T18, availability T19, staff removal T20, time
/// zone T22). Callers hold the locks ADR 0002 asks for, including the cancelling user's row (cancelled_by
/// references it) and each booking's staff row, and have read the bookings under them.
/// </summary>
public sealed class BookingCanceller(BookingDbContext db, OutboxWriter outbox)
{
    /// <summary>
    /// Sets each booking Cancelled (cancelled_at, cancelled_by, reason, version + 1) and stages one
    /// BookingCancelled per booking; the caller's SaveChanges writes the events. Pending reminders are deleted
    /// by the worker on that event (R9).
    /// <para>
    /// One UPDATE for all the bookings (code review 2026-11-20): an R6 change can cancel many, and every round
    /// trip is made while the staff lock is held, keeping that staff member's bookings waiting.
    /// </para>
    /// </summary>
    public async Task CancelAsync(IReadOnlyCollection<Booking> bookings, Business business, Guid cancelledBy, ActorRole role,
        CancellationReason reason, DateTime nowUtc, CancellationToken ct)
    {
        if (bookings.Count == 0) return;
        var ids = bookings.Select(b => b.Id).ToList();

        // Only bookings still Confirmed. Every writer of a booking holds its staff member's lock, so each is
        // unchanged since it was read under the locks (its version too, so version + 1 below matches the event),
        // and a short count means a bug, not a race the caller could retry.
        var updated = await db.Bookings.Where(b => ids.Contains(b.Id) && b.Status == BookingStatus.Confirmed).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Status, BookingStatus.Cancelled)
            .SetProperty(b => b.CancelledAt, nowUtc)
            .SetProperty(b => b.CancelledBy, cancelledBy)
            .SetProperty(b => b.CancellationReason, reason)
            .SetProperty(b => b.Version, b => b.Version + 1), ct);
        if (updated != bookings.Count)
            throw new InvalidOperationException($"{bookings.Count - updated} of {bookings.Count} bookings changed under their locks.");

        foreach (var booking in bookings)
        {
            outbox.Add(new BookingCancelled(booking.Id, booking.Version + 1, business.Id, business.TimeZone, booking.ServiceId,
                booking.CustomerId, new EventSlot(booking.StaffId, booking.StartUtc, booking.EndUtc), new EventActor(cancelledBy, role), reason));
        }
    }
}
