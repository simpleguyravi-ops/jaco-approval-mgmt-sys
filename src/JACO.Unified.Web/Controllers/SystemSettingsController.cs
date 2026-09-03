using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Single admin-editable environment flag -- takes effect immediately (CockpitController
// reads it live before every Clear Log action), no redeploy needed.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class SystemSettingsController(UnifiedDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var settings = await db.SystemSettings.SingleOrDefaultAsync(s => s.Id == 1) ?? new SystemSettings { Id = 1 };
        return View(settings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(bool isProduction)
    {
        var settings = await db.SystemSettings.SingleOrDefaultAsync(s => s.Id == 1);
        if (settings is null)
        {
            settings = new SystemSettings { Id = 1 };
            db.SystemSettings.Add(settings);
        }

        settings.IsProduction = isProduction;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedByUserName = User.Identity?.Name;

        await db.SaveChangesAsync();
        TempData["Success"] = isProduction
            ? "System marked Production -- compliance guardrails (e.g. the Clear Log minimum-retention floor) are now enforced."
            : "System marked Test -- compliance guardrails that would get in the way of development/QA (e.g. the Clear Log minimum-retention floor) are relaxed.";
        return RedirectToAction(nameof(Index));
    }
}
