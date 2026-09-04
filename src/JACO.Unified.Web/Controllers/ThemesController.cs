using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JACO.Unified.Web.Controllers;

// Lets any signed-in user pick their own visual template for JAMS -- unlike most of
// Administration this isn't admin-gated, since it's a personal display preference, not a
// system setting. Stored in a Unified-only cookie rather than Portal's shared "Theme" claim
// (see ThemeCatalog's doc comment) so picking a template here never touches Portal, CR,
// Approval or Sales Discount's own look.
[Authorize]
public sealed class ThemesController : Controller
{
    public const string CookieName = "UnifiedTheme";

    [HttpGet]
    public IActionResult Index()
    {
        var cookieTheme = Request.Cookies[CookieName];
        var claimTheme = User.FindFirst("Theme")?.Value == "fiori" ? "fiori" : "orange";
        ViewBag.Current = ThemeCatalog.IsValid(cookieTheme) ? cookieTheme! : claimTheme;
        return View(ThemeCatalog.Options.ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Apply(string theme)
    {
        if (ThemeCatalog.IsValid(theme))
        {
            Response.Cookies.Append(CookieName, theme, new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps
            });
            TempData["Success"] = $"Template set to {ThemeCatalog.Get(theme).Name}.";
        }
        return RedirectToAction(nameof(Index));
    }
}
