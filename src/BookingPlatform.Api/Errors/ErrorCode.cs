namespace BookingPlatform.Api.Errors;

/// <summary>The ErrorCode enum in specs/04-api.openapi.yaml, serialized by name.</summary>
public enum ErrorCode
{
    ValidationFailed,
    RequestRejected,
    RangeTooLong,
    InviteInvalid,
    ResetLinkInvalid,
    Unauthenticated,
    InvalidCredentials,
    InvalidRefreshToken,
    Forbidden,
    NotFound,
    SlotConflict,
    ConfirmationRequired,
    EmailTaken,
    AlreadyOwner,
    AlreadyStaff,
    InviteNotPending,
    CurrentPasswordIncorrect,
    MinimumNotice,
    CancellationWindowPassed,
    SlotUnavailable,
    StaffNotAssignedToService,
    BookingCancelled,
    CustomerNotFound,
    IdempotencyKeyReused,
    InternalError,
}
