using System.Reflection;
using BookingPlatform.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookingPlatform.Api.Tests;

/// <summary>
/// T7a: change-password is the only /v1/auth/ action that needs a token. [AllowAnonymous]
/// overrides [Authorize], so it must be on each anonymous action, never on the controller.
/// (The fallback policy would demand a token anyway, but [Authorize] states the intent, and
/// these tests catch [AllowAnonymous] creeping back onto the class.)
/// </summary>
public class AuthControllerAttributeTests
{
    private static List<MethodInfo> Actions => typeof(AuthController)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
        .ToList();

    [Fact]
    public void Controller_is_not_anonymous_as_a_whole()
    {
        Assert.Null(typeof(AuthController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(typeof(AuthController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void Change_password_needs_a_token_and_every_other_action_is_anonymous()
    {
        // register, login, accept-invite, forgot-password, reset-password, change-password, refresh, logout
        Assert.Equal(8, Actions.Count);

        var authorized = Assert.Single(Actions, m => m.GetCustomAttribute<AllowAnonymousAttribute>() is null);

        Assert.Equal("change-password", authorized.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(authorized.GetCustomAttribute<AuthorizeAttribute>());
    }
}
