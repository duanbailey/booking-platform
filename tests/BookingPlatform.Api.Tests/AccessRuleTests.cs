using BookingPlatform.Api.Authorization;
using BookingPlatform.Api.Errors;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T3: the 404-versus-403 rule (specs/07-nfr.md "Responses"): 404 when the caller
/// cannot view the resource at all, 403 when they can view it but not perform the action.
/// </summary>
public class AccessRuleTests
{
    [Fact]
    public void Caller_who_cannot_view_gets_404_even_if_the_action_would_be_allowed()
    {
        var error = Assert.Throws<ApiException>(() => Access.Require(canView: false, canAct: true));
        Assert.Equal(404, error.Status);
        Assert.Equal(ErrorCode.NotFound, error.Code);
    }

    [Fact]
    public void Caller_who_can_view_but_not_act_gets_403()
    {
        var error = Assert.Throws<ApiException>(() => Access.Require(canView: true, canAct: false));
        Assert.Equal(403, error.Status);
        Assert.Equal(ErrorCode.Forbidden, error.Code);
    }

    [Fact]
    public void Caller_who_can_view_and_act_passes()
    {
        Access.Require(canView: true, canAct: true);
    }

    [Fact]
    public void Missing_resource_gets_404()
    {
        var error = Assert.Throws<ApiException>(() => Access.Found<string>(null));
        Assert.Equal(404, error.Status);
        Assert.Equal("x", Access.Found("x"));
    }
}
