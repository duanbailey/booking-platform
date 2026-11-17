using BookingPlatform.Api.Observability;

namespace BookingPlatform.Api.Errors;

/// <summary>
/// Turns exceptions into the shared Error response (specs/07-nfr.md "Errors"):
/// an <see cref="ApiException"/> becomes its own status and code; a request Kestrel
/// refuses (e.g. a body over the size limit) becomes RequestRejected with its status;
/// anything else is logged with the request's correlation ID and becomes 500
/// InternalError with a generic message, never the exception's details.
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is no one to answer.
        }
        catch (ApiException exception) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, exception);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            logger.LogInformation(exception, "Request refused: {StatusCode}", exception.StatusCode);
            await WriteAsync(context, exception.StatusCode == StatusCodes.Status400BadRequest
                ? ApiException.ForStatus(StatusCodes.Status400BadRequest)
                : ApiException.RequestRejected(exception.StatusCode));
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, ApiException.ForStatus(StatusCodes.Status500InternalServerError));
        }
    }

    /// <summary>
    /// Discards whatever the endpoint set for a success response (Location, ETag,
    /// Cache-Control, ...) so it never leaks onto the error, keeping only the correlation ID.
    /// </summary>
    private static Task WriteAsync(HttpContext context, ApiException exception)
    {
        context.Response.Clear();
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] = context.GetCorrelationId();
        return ErrorResponses.WriteAsync(context, exception);
    }
}
