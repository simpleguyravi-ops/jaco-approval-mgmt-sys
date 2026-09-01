using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Local, standalone login accounts (see AccountController) -- distinct from UsersRoles,
// which only manages per-Approval-Type Create/Display-All grants for users however they
// happen to sign in (Portal SSO or local). This screen is what actually lets someone sign
// in to Unified directly: creating an account here, or setting a password on one that was
// only ever auto-provisioned from a Portal-SSO login, is what enables that.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class UserAccountsController(UnifiedDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? sort, string dir = "asc")
    {
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var desc = dir == "desc";
        IQueryable<AppUser> query = db.AppUsers;
        query = sort switch
        {
            "Username" => desc ? query.OrderByDescending(u => u.UserName) : query.OrderBy(u => u.UserName),
            "Department" => desc ? query.OrderByDescending(u => u.Department) : query.OrderBy(u => u.Department),
            "Status" => desc ? query.OrderByDescending(u => u.IsActive) : query.OrderBy(u => u.IsActive),
            _ => query.OrderBy(u => u.DisplayName)
        };
        return View(await query.ToListAsync());
    }

    public async Task<IActionResult> Export()
    {
        var users = await db.AppUsers.OrderBy(u => u.DisplayName).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(users,
            ["Name", "Username", "Department", "Email", "Local Login", "Admin", "Auditor", "Status"],
            u => [u.DisplayName, u.UserName, u.Department ?? "", u.Email ?? "", u.PasswordHash is null ? "No" : "Yes", u.IsAdmin ? "Yes" : "No", u.IsAuditor ? "Yes" : "No", u.IsActive ? "Active" : "Disabled"]);
        return File(bytes, "text/csv", $"user-accounts-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet]
    public IActionResult Create() => View("Edit", new UserAccountEditViewModel { IsActive = true });

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        return View(new UserAccountEditViewModel
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            Department = user.Department,
            Email = user.Email,
            IsActive = user.IsActive,
            IsAdmin = user.IsAdmin,
            IsAuditor = user.IsAuditor,
            HasPassword = user.PasswordHash is not null
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserAccountEditViewModel model)
    {
        model.UserName = (model.UserName ?? "").Trim();
        model.DisplayName = (model.DisplayName ?? "").Trim();

        if (string.IsNullOrWhiteSpace(model.UserName) || string.IsNullOrWhiteSpace(model.DisplayName))
        {
            TempData["Error"] = "Username and Display Name are required.";
            return View("Edit", model);
        }
        // Same floor as self-service Change Password -- an admin-set password reaches the
        // same account and should be held to the same minimum, not a weaker one.
        if (!string.IsNullOrEmpty(model.NewPassword) && model.NewPassword.Length < 8)
        {
            TempData["Error"] = "Password must be at least 8 characters.";
            return View("Edit", model);
        }

        AppUser user;
        if (model.Id == 0)
        {
            if (string.IsNullOrWhiteSpace(model.NewPassword))
            {
                TempData["Error"] = "A password is required for a new account.";
                return View("Edit", model);
            }
            if (await db.AppUsers.AnyAsync(u => u.UserName == model.UserName))
            {
                TempData["Error"] = "That username already exists.";
                return View("Edit", model);
            }
            var (hash, salt) = PasswordHasher.Hash(model.NewPassword);
            user = new AppUser { UserName = model.UserName, PasswordHash = hash, PasswordSalt = salt, MustChangePassword = true };
            db.AppUsers.Add(user);
        }
        else
        {
            user = await db.AppUsers.SingleAsync(u => u.Id == model.Id);
            if (!string.IsNullOrWhiteSpace(model.NewPassword))
            {
                var (hash, salt) = PasswordHasher.Hash(model.NewPassword);
                user.PasswordHash = hash;
                user.PasswordSalt = salt;
                user.MustChangePassword = true;
                // A password reset is also a reasonable moment to clear any lockout --
                // otherwise a locked-out user with a freshly reset password would still
                // have to wait out the lockout window to use it.
                user.FailedLoginCount = 0;
                user.LockedUntil = null;
            }
        }

        user.DisplayName = model.DisplayName;
        user.Department = model.Department;
        user.Email = model.Email;
        user.IsActive = model.IsActive;
        user.IsAdmin = model.IsAdmin;
        user.IsAuditor = model.IsAuditor;

        db.AuditLogs.Add(new AuditLog { ActionCode = model.Id == 0 ? "UserAccountCreated" : "UserAccountUpdated", DetailsJson = model.UserName, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        TempData["Success"] = model.Id == 0 ? $"Account '{model.UserName}' created." : "Saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlock(int id)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, ActionCode = "AccountUnlocked", DetailsJson = User.Identity?.Name, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        TempData["Success"] = $"'{user.DisplayName}' unlocked.";
        return RedirectToAction(nameof(Index));
    }
}
