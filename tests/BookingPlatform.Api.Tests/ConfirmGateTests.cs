using BookingPlatform.Api.Bookings;
using BookingPlatform.Api.Errors;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T15: the confirm-before-acting pattern (OpenAPI "Confirm-before-acting"), shared by R4 now and R6/R2
/// later. Decisions 2026-11-20 (plan/tasks.md): the confirmed IDs must equal the affected set exactly; an
/// empty set goes ahead whether or not confirm was sent; affectedBookings ordered by start, then id.
/// </summary>
public class ConfirmGateTests
{
    private static readonly DateTime Nine = new(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Staff = Guid.NewGuid();

    private static AffectedBooking At(DateTime start, Guid? id = null) => new(id ?? Guid.NewGuid(), Staff, start, start.AddHours(1));

    private static ApiException Refused(IReadOnlyCollection<AffectedBooking> affected, IReadOnlyCollection<Guid>? confirmed) =>
        Assert.Throws<ApiException>(() => ConfirmGate.Require(affected, confirmed, "R4", "Confirm this."));

    [Fact]
    public void Nothing_affected_goes_ahead_with_or_without_confirm()
    {
        ConfirmGate.Require([], null, "R4", "Confirm this.");
        ConfirmGate.Require([], [Guid.NewGuid()], "R4", "Confirm this.");
    }

    [Fact]
    public void Affected_bookings_without_confirm_are_ConfirmationRequired_with_the_list()
    {
        var booking = At(Nine);

        var error = Refused([booking], null);

        Assert.Equal(409, error.Status);
        Assert.Equal(ErrorCode.ConfirmationRequired, error.Code);
        Assert.Equal("R4", error.Rule);
        Assert.Equal("Confirm this.", error.Message);
        Assert.Equal([booking], Assert.IsType<ConfirmationDetails>(error.Details).AffectedBookings);
    }

    [Fact]
    public void The_list_is_ordered_by_start_then_id()
    {
        var (low, high) = (Guid.Parse("10000000-0000-0000-0000-000000000000"), Guid.Parse("f0000000-0000-0000-0000-000000000000"));
        var later = At(Nine.AddHours(2));
        var nineHigh = At(Nine, high);
        var nineLow = At(Nine, low);

        var error = Refused([later, nineHigh, nineLow], null);

        Assert.Equal([nineLow, nineHigh, later], ((ConfirmationDetails)error.Details).AffectedBookings);
    }

    [Fact]
    public void Exactly_the_affected_set_goes_ahead_in_any_order()
    {
        var (a, b) = (At(Nine), At(Nine.AddHours(1)));

        ConfirmGate.Require([a, b], [b.BookingId, a.BookingId], "R4", "Confirm this.");
    }

    [Fact]
    public void A_subset_or_a_superset_is_ConfirmationRequired_with_the_current_list()
    {
        var (a, b) = (At(Nine), At(Nine.AddHours(1)));

        Assert.Equal([a, b], ((ConfirmationDetails)Refused([a, b], [a.BookingId]).Details).AffectedBookings);
        Assert.Equal([a], ((ConfirmationDetails)Refused([a], [a.BookingId, b.BookingId]).Details).AffectedBookings);
        Assert.Equal([a], ((ConfirmationDetails)Refused([a], [b.BookingId]).Details).AffectedBookings);
    }
}
