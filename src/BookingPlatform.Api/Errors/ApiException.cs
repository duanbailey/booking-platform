namespace BookingPlatform.Api.Errors;

/// <summary>
/// An expected error, turned into the shared Error response by <see cref="ErrorHandlingMiddleware"/>.
/// Its message is shown to the client, so it must never contain internal details.
/// </summary>
public sealed class ApiException(int status, ErrorCode code, string message, string? rule = null, object? details = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public ErrorCode Code { get; } = code;
    /// <summary>Business rule ID (e.g. "R3"), or null for errors that are not business rules.</summary>
    public string? Rule { get; } = rule;
    public object Details { get; } = details ?? new { };

    public ErrorResponse ToResponse() => new(Code, Message, Rule, Details);

    public static ApiException Unauthenticated() =>
        new(StatusCodes.Status401Unauthorized, ErrorCode.Unauthenticated, "A valid access token is required.");

    /// <summary>The caller can view the resource but may not perform the action (specs/07-nfr.md).</summary>
    public static ApiException Forbidden() =>
        new(StatusCodes.Status403Forbidden, ErrorCode.Forbidden, "You are not allowed to do this.");

    /// <summary>The resource does not exist, or the caller may not view it (specs/07-nfr.md).</summary>
    public static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ErrorCode.NotFound, "The resource was not found.");

    /// <summary>Refused at the HTTP level before reaching an operation (405, 406, 408, 413, 415, 431, ...).</summary>
    public static ApiException RequestRejected(int status) =>
        new(status, ErrorCode.RequestRejected, status switch
        {
            StatusCodes.Status405MethodNotAllowed => "This method is not allowed on this path.",
            StatusCodes.Status406NotAcceptable => "The requested response format is not available; accept application/json.",
            StatusCodes.Status408RequestTimeout => "The request took too long to arrive.",
            StatusCodes.Status413PayloadTooLarge => "The request body is too large.",
            StatusCodes.Status415UnsupportedMediaType => "Send the request body as application/json.",
            StatusCodes.Status429TooManyRequests => "Too many requests; wait for the Retry-After seconds and try again.",
            StatusCodes.Status431RequestHeaderFieldsTooLarge => "The request headers are too large.",
            _ => "The request was rejected.",
        });

    /// <summary>
    /// The error for a response that has an error status but no body yet (authentication
    /// challenges, unknown routes, wrong methods, ...), so every error has the shared shape.
    /// </summary>
    public static ApiException ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => new(status, ErrorCode.ValidationFailed, "The request is not valid."),
        StatusCodes.Status401Unauthorized => Unauthenticated(),
        StatusCodes.Status403Forbidden => Forbidden(),
        StatusCodes.Status404NotFound => NotFound(),
        >= 500 => new(status, ErrorCode.InternalError, InternalErrorMessage),
        _ => RequestRejected(status),
    };

    public const string InternalErrorMessage = "Something went wrong. Quote the X-Correlation-Id header if you report this.";

    /// <summary>Unknown email, wrong password or no password yet, without saying which (specs/07-nfr.md).</summary>
    public static ApiException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, ErrorCode.InvalidCredentials, "The email or password is incorrect.");

    public static ApiException EmailTaken() =>
        new(StatusCodes.Status409Conflict, ErrorCode.EmailTaken, "An account with this email already exists.");

    /// <summary>Story "Admin creates an owner": each user owns at most one business.</summary>
    public static ApiException AlreadyOwner() =>
        new(StatusCodes.Status409Conflict, ErrorCode.AlreadyOwner, "This account already owns a business.");

    /// <summary>Unknown, expired, used or revoked, without saying which (ADR 0010).</summary>
    public static ApiException InvalidRefreshToken() =>
        new(StatusCodes.Status401Unauthorized, ErrorCode.InvalidRefreshToken, "The refresh token is not valid.");

    /// <summary>Unknown, expired, used or superseded invite link, without saying which (specs/04-api.openapi.yaml).</summary>
    public static ApiException InviteInvalid() =>
        new(StatusCodes.Status400BadRequest, ErrorCode.InviteInvalid, "This invite link is no longer valid.");

    /// <summary>Unknown, expired, used or superseded recovery link, without saying which (specs/04-api.openapi.yaml).</summary>
    public static ApiException ResetLinkInvalid() =>
        new(StatusCodes.Status400BadRequest, ErrorCode.ResetLinkInvalid, "This password reset link is no longer valid.");

    /// <summary>Story "User can change their password": the current password is wrong (or empty).</summary>
    public static ApiException CurrentPasswordIncorrect() =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCode.CurrentPasswordIncorrect, "The current password is incorrect.");

    /// <summary>Resend refused: the account already has a password.</summary>
    public static ApiException InviteNotPending() =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCode.InviteNotPending, "This account already has a password.");
}
