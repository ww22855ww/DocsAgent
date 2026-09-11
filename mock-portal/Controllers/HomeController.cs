using Microsoft.AspNetCore.Mvc;

namespace MockPortal.Controllers;

public class HomeController : Controller
{
    // Phase 0 placeholder. Phase 1 replaces this with the real
    // Login -> Sidebar -> Query -> Result -> Download flow.
    public IActionResult Index() => View();
}
