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

/// <summary>
/// A password recovery link (T7a, specs/05-data-model.md password_resets). Works like
/// <see cref="UserInvite"/>: the API inserts the row, the worker sets the token hash and the
/// 1-hour expiry when it sends the email.
/// </summary>
public class PasswordReset
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Set by the worker when it sends the email; the token itself is never stored.</summary>
    public string? TokenHash { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A refresh token for a login session (ADR 0010). Only the token's SHA-256 hash is stored.</summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Groups the tokens from one login and its refreshes; not a foreign key.</summary>
    public Guid FamilyId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Set when the token is exchanged; presenting it again revokes the family.</summary>
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
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

/// <summary>
/// The range of each business setting (specs/05-data-model.md businesses). The maxima only
/// keep date arithmetic in range (decided 2026-11-19); the advance-booking window is not capped
/// for performance (ADR 0005).
/// </summary>
public static class BusinessLimits
{
    public const int MaxMinNoticeMinutes = 525_600;     // one year, R3
    public const int MaxCancellationWindowHours = 8_760; // one year, R4
    public const int MaxMaxAdvanceDays = 3_650;          // ten years, R8
    public const int MaxSlotStepMinutes = 1_440;         // one day, R8
}

/// <summary>The range of a service's duration and buffers (decided 2026-11-19; price: PriceAttribute).</summary>
public static class ServiceLimits
{
    /// <summary>Availability blocks lie within a day and bound the appointment (R8).</summary>
    public const int MaxDurationMinutes = 1_440;
    public const int MaxBufferMinutes = 1_440;

    /// <summary>
    /// The longest a booking's blocked interval can be (duration plus both buffers at the maxima), so a
    /// query for bookings overlapping a window can bound blocked_start_utc from below and use the
    /// (staff_id, status, blocked_start_utc) index as a range scan. Bookings must keep within these maxima.
    /// </summary>
    public static readonly TimeSpan MaxBlockedSpan = TimeSpan.FromMinutes(MaxDurationMinutes + 2 * MaxBufferMinutes);
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

    /// <summary>
    /// Database-assigned on insert (AUTO_INCREMENT); the relay publishes each message group in seq order
    /// (decided 2026-11-23, specs/05-data-model.md). Never written by the application.
    /// </summary>
    public long Seq { get; set; }

    /// <summary>Failed publish attempts so far (T21); reset never, kept for diagnosis.</summary>
    public int Attempts { get; set; }

    /// <summary>The last publish failure, truncated; null once published or if none.</summary>
    public string? LastError { get; set; }

    /// <summary>When a failed row may be tried again (backoff); null if it never failed.</summary>
    public DateTime? NextAttemptAt { get; set; }
}

public static class OutboxMessageLimits
{
    public const int LastErrorLength = 1000;
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

/// <summary>
/// T29: the Idempotency-Key of a booking created with one. Keys belong to the caller (user_id) and count for
/// 24 hours after created_at; only created bookings get a row (specs/05-data-model.md).
/// </summary>
public class IdempotencyRecord
{
    public Guid UserId { get; set; }
    public required string IdempotencyKey { get; set; }
    /// <summary>Lowercase hex SHA-256 of the request's normalised fields; refuses the key for a different request.</summary>
    public required string RequestHash { get; set; }
    public Guid BookingId { get; set; }
    public DateTime CreatedAt { get; set; }
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
    Invite, AccessGranted, PasswordReset, PasswordChanged, Confirmation, Rescheduled, Cancelled, LocalTimeChanged, RebookedInsideWindow,
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
