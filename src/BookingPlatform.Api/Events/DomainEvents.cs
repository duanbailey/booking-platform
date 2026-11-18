using BookingPlatform.Api.Data;

namespace BookingPlatform.Api.Events;

/// <summary>
/// An event the API writes to the outbox (specs/06-events.md). The public properties of
/// each derived record are the envelope's <c>data</c>; each has a JSON Schema in
/// specs/events/. The members here are internal, so they are not serialized: they fill
/// the envelope and the outbox row. ReminderDue is not here: EventBridge Scheduler sends it.
/// </summary>
public abstract record DomainEvent
{
    internal abstract string EventType { get; }

    internal abstract int SchemaVersion { get; }

    /// <summary>The SQS FIFO message group ("FIFO queue" in specs/06-events.md).</summary>
    internal abstract Guid MessageGroupId { get; }
}

/// <summary>A staff member and a half-open interval [StartUtc, EndUtc), without buffers (R1).</summary>
public sealed record EventSlot(Guid StaffId, DateTime StartUtc, DateTime EndUtc);

/// <summary>The user who made a change, and the role they acted in.</summary>
public sealed record EventActor(Guid UserId, ActorRole Role);

public enum GrantedRole { Owner, Staff }

public enum PasswordChangeMethod { Changed, Reset }

public sealed record BookingCreated(
    Guid BookingId, int BookingVersion, Guid BusinessId, string BusinessTimeZone, Guid ServiceId,
    Guid CustomerId, EventSlot Slot, EventActor CreatedBy) : DomainEvent
{
    internal override string EventType => nameof(BookingCreated);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => BookingId;
}

/// <summary>R5, ADR 0003: one event per reschedule, never with BookingCancelled or BookingCreated.</summary>
public sealed record BookingRescheduled(
    Guid BookingId, int BookingVersion, Guid BusinessId, string BusinessTimeZone, Guid ServiceId,
    Guid CustomerId, EventSlot OldSlot, EventSlot NewSlot, EventActor RescheduledBy) : DomainEvent
{
    internal override string EventType => nameof(BookingRescheduled);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => BookingId;
}

/// <summary>One event per cancelled booking, for any reason, including R2 and R6.</summary>
public sealed record BookingCancelled(
    Guid BookingId, int BookingVersion, Guid BusinessId, string BusinessTimeZone, Guid ServiceId,
    Guid CustomerId, EventSlot Slot, EventActor CancelledBy, CancellationReason Reason) : DomainEvent
{
    internal override string EventType => nameof(BookingCancelled);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => BookingId;
}

/// <summary>R2: a time zone change leaves a booking at a different local time.</summary>
public sealed record BookingLocalTimeChanged(
    Guid BookingId, Guid BusinessId, Guid CustomerId, EventSlot Slot, string OldTimeZone,
    string NewTimeZone, Guid TimeZoneChangeId) : DomainEvent
{
    internal override string EventType => nameof(BookingLocalTimeChanged);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => BookingId;
}

/// <summary>R4: sent in addition to the new booking's BookingCreated.</summary>
public sealed record RebookedInsideWindow(
    Guid BusinessId, string BusinessTimeZone, Guid CustomerId, Guid OriginalBookingId,
    EventSlot OriginalSlot, Guid NewBookingId, EventSlot NewSlot) : DomainEvent
{
    internal override string EventType => nameof(RebookedInsideWindow);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => OriginalBookingId;
}

/// <summary>InviteId is null when the user already has a password; it is always serialized.</summary>
public sealed record AccessGranted(
    Guid UserId, Guid BusinessId, GrantedRole Role, Guid? InviteId, Guid GrantedByUserId) : DomainEvent
{
    internal override string EventType => nameof(AccessGranted);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => UserId;
}

public sealed record PasswordResetRequested(Guid UserId, Guid PasswordResetId) : DomainEvent
{
    internal override string EventType => nameof(PasswordResetRequested);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => UserId;
}

public sealed record PasswordChanged(Guid UserId, PasswordChangeMethod Method) : DomainEvent
{
    internal override string EventType => nameof(PasswordChanged);
    internal override int SchemaVersion => 1;
    internal override Guid MessageGroupId => UserId;
}
