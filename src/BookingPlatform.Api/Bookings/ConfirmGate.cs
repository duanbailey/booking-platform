using BookingPlatform.Api.Errors;

namespace BookingPlatform.Api.Bookings;

/// <summary>The AffectedBooking schema: a booking a change would affect, without buffers.</summary>
public sealed record AffectedBooking(Guid BookingId, Guid StaffId, DateTime StartUtc, DateTime EndUtc);

/// <summary>The details of 409 ConfirmationRequired.</summary>
public sealed record ConfirmationDetails(IReadOnlyList<AffectedBooking> AffectedBookings);

/// <summary>
/// Confirm-before-acting (OpenAPI "Confirm-before-acting"; R4 from T15, R6 and R2 from T18-T22). The caller
/// works out the affected set under the staff lock (ADR 0002) and calls <see cref="Require"/> before making
/// the change. Decisions 2026-11-20: the confirmed IDs must equal that set exactly, so a booking the user was
/// never shown is never acted on; an empty set goes ahead whether or not confirm was sent; the list is
/// ordered by start, then id.
/// </summary>
public static class ConfirmGate
{
    /// <exception cref="ApiException">409 ConfirmationRequired with the current list, unless it may go ahead.</exception>
    public static void Require(IReadOnlyCollection<AffectedBooking> affected, IReadOnlyCollection<Guid>? confirmed, string rule, string message)
    {
        if (affected.Count == 0) return;
        if (confirmed is not null && confirmed.ToHashSet().SetEquals(affected.Select(b => b.BookingId))) return;

        var ordered = affected.OrderBy(b => b.StartUtc).ThenBy(b => b.BookingId.ToString(), StringComparer.Ordinal).ToList();
        throw ApiException.ConfirmationRequired(rule, message, new ConfirmationDetails(ordered));
    }
}
