using Microsoft.AspNetCore.Mvc;
using MockPortal.Infrastructure;

namespace MockPortal.Controllers;

public class AccountController : Controller
{
    // Demo credentials, documented in roadmap.md section 4.4.
    private const string ValidUser = "admin";
    private const string ValidPassword = "123456";

    [HttpGet]
    public IActionResult Login()
    {
        if (!string.IsNullOrEmpty(HttpContext.Session.GetString(RequireLoginAttribute.SessionKey)))
            return RedirectToAction("Query", "Documents");

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Login(string username, string password)
    {
        if (username == ValidUser && password == ValidPassword)
        {
            HttpContext.Session.SetString(RequireLoginAttribute.SessionKey, username);
            return RedirectToAction("Query", "Documents");
        }

        ViewData["Error"] = "Invalid username or password.";
        ViewData["Username"] = username;
        return View();
    }

    [HttpGet]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Login");
    }
}
