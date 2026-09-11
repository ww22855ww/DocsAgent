using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MockPortal.Infrastructure;

/// <summary>
/// Session gate. Unauthenticated requests are bounced to the login page, which
/// is what makes Playwright have to log in before it can query anything.
/// </summary>
public sealed class RequireLoginAttribute : ActionFilterAttribute
{
    public const string SessionKey = "portal_user";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.Session.GetString(SessionKey);
        if (string.IsNullOrEmpty(user))
        {
            context.Result = new RedirectToActionResult("Login", "Account", null);
            return;
        }
        base.OnActionExecuting(context);
    }
}
