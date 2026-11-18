using BookingPlatform.Api.Data;
using BookingPlatform.Api.Events;

namespace BookingPlatform.Tests.Shared;

/// <summary>
/// One populated instance of every event the API writes, keyed by event type. Shared by
/// the unit and integration test projects.
/// </summary>
public static class SampleEvents
{
    public static readonly Guid BookingId = Guid.Parse("0190a000-0000-7000-8000-000000000001");
    public static readonly Guid OtherBookingId = Guid.Parse("0190a000-0000-7000-8000-000000000002");
    public static readonly Guid UserId = Guid.Parse("0190a000-0000-7000-8000-000000000003");

    private static readonly Guid Business = Guid.Parse("0190a000-0000-7000-8000-000000000010");
    private static readonly Guid Service = Guid.Parse("0190a000-0000-7000-8000-000000000011");
    private static readonly Guid Staff = Guid.Parse("0190a000-0000-7000-8000-000000000012");
    private static readonly Guid Customer = Guid.Parse("0190a000-0000-7000-8000-000000000013");
    private static readonly Guid Owner = Guid.Parse("0190a000-0000-7000-8000-000000000014");

    private static DateTime Utc(int day, int hour) => new(2026, 12, day, hour, 0, 0, DateTimeKind.Utc);

    private static readonly EventSlot Slot = new(Staff, Utc(1, 9), Utc(1, 10));
    private static readonly EventSlot LaterSlot = new(Staff, Utc(2, 14), Utc(2, 15));

    public static readonly IReadOnlyDictionary<string, DomainEvent> All = new Dictionary<string, DomainEvent>
    {
        ["BookingCreated"] = new BookingCreated(BookingId, 1, Business, "Europe/London", Service, Customer, Slot,
            new EventActor(Customer, ActorRole.Customer)),
        ["BookingRescheduled"] = new BookingRescheduled(BookingId, 2, Business, "Europe/London", Service, Customer,
            Slot, LaterSlot, new EventActor(Staff, ActorRole.Staff)),
        ["BookingCancelled"] = new BookingCancelled(BookingId, 3, Business, "Europe/London", Service, Customer, Slot,
            new EventActor(Owner, ActorRole.Owner), CancellationReason.TimeOff),
        ["BookingLocalTimeChanged"] = new BookingLocalTimeChanged(BookingId, Business, Customer, Slot,
            "Europe/London", "America/New_York", Guid.Parse("0190a000-0000-7000-8000-000000000020")),
        ["RebookedInsideWindow"] = new RebookedInsideWindow(Business, "Europe/London", Customer,
            OtherBookingId, Slot, BookingId, LaterSlot),
        ["AccessGranted"] = new AccessGranted(UserId, Business, GrantedRole.Staff, null, Owner),
        ["PasswordResetRequested"] = new PasswordResetRequested(UserId, Guid.Parse("0190a000-0000-7000-8000-000000000021")),
        ["PasswordChanged"] = new PasswordChanged(UserId, PasswordChangeMethod.Reset),
    };

    /// <summary>The message group each sample must get (specs/06-events.md "FIFO queue").</summary>
    public static readonly IReadOnlyDictionary<string, Guid> ExpectedMessageGroup = new Dictionary<string, Guid>
    {
        ["BookingCreated"] = BookingId,
        ["BookingRescheduled"] = BookingId,
        ["BookingCancelled"] = BookingId,
        ["BookingLocalTimeChanged"] = BookingId,
        ["RebookedInsideWindow"] = OtherBookingId, // the original booking
        ["AccessGranted"] = UserId,
        ["PasswordResetRequested"] = UserId,
        ["PasswordChanged"] = UserId,
    };

    public static TheoryData<string> Names() => new(All.Keys);
}
