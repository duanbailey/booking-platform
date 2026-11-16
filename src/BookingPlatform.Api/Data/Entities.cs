namespace BookingPlatform.Api.Data;

// Persistence entities for specs/05-data-model.md. Every DateTime is UTC (R2);
// local dates and times (availability) use DateOnly and TimeOnly in the business's zone.

public class User
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    /// <summary>Null until an invite is accepted; an account without a password cannot log in.</summary>
    public string? PasswordHash { get; set; }
    public required string Name { get; set; }
    /// <summary>The user's IANA time zone (R2).</summary>
    public required string TimeZone { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserInvite
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Set by the worker when it sends the invite; the token itself is never stored.</summary>
    public string? TokenHash { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PlatformAdmin
{
    public Guid UserId { get; set; }
    /// <summary>
    /// The existing admin named when the operator script granted the role; null only for the
    /// first (bootstrap) admin. Admins are never granted through the API.
    /// </summary>
    public Guid? GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; }
}

public class Business
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid OwnerUserId { get; set; }
    /// <summary>IANA time zone; the reference for availability (R2).</summary>
    public required string TimeZone { get; set; }
    public int MinNoticeMinutes { get; set; } = BusinessDefaults.MinNoticeMinutes;
    public int CancellationWindowHours { get; set; } = BusinessDefaults.CancellationWindowHours;
    public int MaxAdvanceDays { get; set; } = BusinessDefaults.MaxAdvanceDays;
    public int SlotStepMinutes { get; set; } = BusinessDefaults.SlotStepMinutes;
    public DateTime CreatedAt { get; set; }
}

public static class BusinessDefaults
{
    public const int MinNoticeMinutes = 120;       // R3
    public const int CancellationWindowHours = 24; // R4
    public const int MaxAdvanceDays = 21;          // R8, ADR 0005
    public const int SlotStepMinutes = 30;         // R8
}

public class Service
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public required string Name { get; set; }
    public int DurationMinutes { get; set; }
    public int BufferBeforeMinutes { get; set; }
    public int BufferAfterMinutes { get; set; }
    public string? Description { get; set; }
    /// <summary>Display only; payments are a non-goal.</summary>
    public decimal Price { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Staff
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BusinessId { get; set; }
    /// <summary>Set on removal instead of deleting the row; cleared when re-added.</summary>
    public DateTime? RemovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class StaffService
{
    public Guid StaffId { get; set; }
    public Guid ServiceId { get; set; }
}

/// <summary>A weekly availability block, in the business's local time (R2, R8).</summary>
public class AvailabilityRule
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartLocal { get; set; }
    public TimeOnly EndLocal { get; set; }
}

/// <summary>
/// A date exception (R8). A row with both times null marks the whole day unavailable;
/// otherwise it is one available range for that date.
/// </summary>
public class AvailabilityException
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly? StartLocal { get; set; }
    public TimeOnly? EndLocal { get; set; }
}

public class TimeOff
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Append-only record of business time zone changes (R2).</summary>
public class TimeZoneChange
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public required string OldTimeZone { get; set; }
    public required string NewTimeZone { get; set; }
    public Guid ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
}

public class OutboxMessage
{
    /// <summary>The event's eventId.</summary>
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public int SchemaVersion { get; set; }
    public required string MessageGroupId { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public enum BookingStatus { Confirmed, Cancelled }

public enum CancellationReason { CustomerCancelled, BusinessCancelled, TimeOff, AvailabilityChanged, StaffRemoved }

public class Booking
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid CustomerId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>Start minus the before-buffer, fixed when booked or rescheduled (R1, ADR 0004).</summary>
    public DateTime BlockedStartUtc { get; set; }
    /// <summary>End plus the after-buffer, fixed when booked or rescheduled (R1, ADR 0004).</summary>
    public DateTime BlockedEndUtc { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public CancellationReason? CancellationReason { get; set; }
    /// <summary>Optimistic concurrency for updates to an existing booking; overlap is ADR 0002's job.</summary>
    public int Version { get; set; }
}

public enum BookingHistoryType { Created, Rescheduled }

public enum ActorRole { Owner, Staff, Customer }

public class BookingHistory
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public BookingHistoryType Type { get; set; }
    public Guid ActorUserId { get; set; }
    /// <summary>The role the actor acted in, fixed at the time (a user can hold several).</summary>
    public ActorRole ActorRole { get; set; }
    public DateTime At { get; set; }
    public Guid StaffId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public Guid? PreviousStaffId { get; set; }
    public DateTime? PreviousStartUtc { get; set; }
    public DateTime? PreviousEndUtc { get; set; }
    /// <summary>True if this creation or reschedule used the R3 override.</summary>
    public bool MinimumNoticeOverridden { get; set; }
}

public enum NotificationKind
{
    Invite, AccessGranted, Confirmation, Rescheduled, Cancelled, LocalTimeChanged, RebookedInsideWindow,
    Reminder24h, Reminder1h,
}

public enum NotificationStatus { Pending, Sent, Failed }

public class Notification
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid RecipientUserId { get; set; }
    public NotificationKind Kind { get; set; }
    public NotificationStatus Status { get; set; }
    public string? SesMessageId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
