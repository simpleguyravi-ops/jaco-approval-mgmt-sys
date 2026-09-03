using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Admin-editable SMTP configuration -- takes effect immediately (MailSender reads it live
// on every send), no redeploy needed.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class EmailSettingsController(UnifiedDbContext db, MailSender mailSender, EmailPasswordProtector protector) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var settings = await db.EmailSettings.SingleOrDefaultAsync(s => s.Id == 1) ?? new EmailSettings { Id = 1 };
        return View(settings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(EmailSettings model)
    {
        var settings = await db.EmailSettings.SingleOrDefaultAsync(s => s.Id == 1);
        if (settings is null)
        {
            settings = new EmailSettings { Id = 1 };
            db.EmailSettings.Add(settings);
        }

        settings.Enabled = model.Enabled;
        settings.Host = model.Host ?? "";
        settings.Port = model.Port > 0 ? model.Port : 587;
        settings.UseTls = model.UseTls;
        settings.From = model.From ?? "";
        settings.Username = model.Username;
        // A blank Password field means "keep the current one" -- the saved password is
        // never rendered back into the form (not even in a hidden field), so this is the
        // only way to change every other setting without having to re-type it. Encrypted
        // at rest via the shared Data Protection key ring, not stored in plaintext.
        if (!string.IsNullOrEmpty(model.Password)) settings.Password = protector.Protect(model.Password);
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedByUserName = User.Identity?.Name;

        await db.SaveChangesAsync();
        TempData["Success"] = "Email configuration saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTest(string testAddress)
    {
        if (string.IsNullOrWhiteSpace(testAddress))
        {
            TempData["Error"] = "Enter an address to send the test email to.";
            return RedirectToAction(nameof(Index));
        }

        var (sent, error) = await mailSender.SendAsync(testAddress, "JAMS -- test email", "<p>This is a test email from JAMS's Email Configuration screen.</p>");
        TempData[sent ? "Success" : "Error"] = sent ? $"Test email sent to {testAddress}." : $"Not sent: {error}";
        return RedirectToAction(nameof(Index));
    }
}
