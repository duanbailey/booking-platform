using System.Diagnostics.CodeAnalysis;
using BookingPlatform.Api.Errors;

namespace BookingPlatform.Api.Authorization;

/// <summary>The 404-versus-403 rule (specs/07-nfr.md "Responses").</summary>
public static class Access
{
    /// <summary>404 when the resource does not exist.</summary>
    public static T Found<T>([NotNull] T? resource) where T : class =>
        resource ?? throw ApiException.NotFound();

    /// <summary>
    /// 404 when the caller cannot view the resource at all, so its existence is never
    /// revealed; 403 when they can view it but may not perform the action.
    /// </summary>
    public static void Require(bool canView, bool canAct)
    {
        if (!canView) throw ApiException.NotFound();
        if (!canAct) throw ApiException.Forbidden();
    }
}
