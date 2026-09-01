using System.Security.Claims;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Standalone login, independent of Portal SSO -- a user with a local password set here
// (see UserAccountsController) can sign straight into Unified without visiting Portal
// first. Issues the SAME shared cookie (.JACO.Auth, same Data Protection key ring already
// configured in Program.cs) that Portal issues, so a Unified-originated login is still
// recognized platform-wide -- this app just becomes one more place you can sign in from,
// not a second, disconnected auth system. A user who already arrives with a valid shared
// cookie from Portal is unaffected: this controller is simply never hit for them.
public sealed class AccountController(UnifiedDbContext db, IConfiguration configuration) : Controller
{
    // Account lockout: after this many consecutive failed local-login attempts, the
    // account is locked for LockoutDuration. Only applies to local login (see Login
    // below) -- has no effect on Portal SSO, which has its own independent auth path.
    const int MaxFailedAttempts = 5;
    static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // A hash/salt pair nobody will ever match, computed once at startup via the real
    // Hash() function (so it's valid base64 of the right length, not just any string) --
    // spending the same ~100k PBKDF2 iterations on a nonexistent username as on a wrong
    // password for a real one, so response timing can't be used to enumerate valid
    // usernames.
    static readonly (string hash, string salt) DummyCredential = PasswordHasher.Hash(Guid.NewGuid().ToString());

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Requests");
        // No fallback on purpose -- a standalone deployment (no other JACO app reachable to
        // SSO with) simply omits this config key, and the view hides the link entirely
        // rather than pointing at a Portal that doesn't exist for that environment.
        ViewBag.PortalHomeUrl = configuration["SharedAuth:PortalHomeUrl"];
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.UserName == model.UserName);
        var now = DateTime.UtcNow;

        if (user is not null && user.LockedUntil is not null)
        {
            if (user.LockedUntil > now)
            {
                var minutesLeft = Math.Ceiling((user.LockedUntil.Value - now).TotalMinutes);
                ModelState.AddModelError("", $"This account is temporarily locked after too many failed sign-in attempts. Try again in about {minutesLeft} minute(s), or ask an administrator to unlock it.");
                return View(model);
            }
            // Lockout window has passed -- clear it so a correct password below succeeds normally.
            user.LockedUntil = null;
            user.FailedLoginCount = 0;
        }

        // Always run a real PBKDF2 verification, even for a username that doesn't exist
        // or has no local password set -- otherwise this branch returns near-instantly
        // while a wrong-password-for-a-real-account attempt takes ~100k iterations
        // longer, and that timing gap is enough to enumerate valid usernames.
        var passwordOk = user is { PasswordHash: not null, PasswordSalt: not null }
            ? PasswordHasher.Verify(model.Password, user.PasswordHash, user.PasswordSalt)
            : PasswordHasher.Verify(model.Password, DummyCredential.hash, DummyCredential.salt);

        if (user is null || !user.IsActive || user.PasswordHash is null || user.PasswordSalt is null || !passwordOk)
        {
            if (user is not null)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= MaxFailedAttempts)
                {
                    user.LockedUntil = now.Add(LockoutDuration);
                    db.AuditLogs.Add(new Core.Models.AuditLog { UserId = user.Id, ActionCode = "AccountLocked", DetailsJson = $"{MaxFailedAttempts} consecutive failed sign-in attempts", CreatedAt = now });
                }
            }
            db.AuditLogs.Add(new Core.Models.AuditLog { ActionCode = "LoginFailed", DetailsJson = model.UserName, CreatedAt = now });
            await db.SaveChangesAsync();
            ModelState.AddModelError("", "Invalid username or password.");
            return View(model);
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new("DisplayName", user.DisplayName),
            new("Department", user.Department ?? ""),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "UNIFIED_ADMIN"));
        if (user.IsAuditor) claims.Add(new Claim(ClaimTypes.Role, "UNIFIED_AUDITOR"));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = model.RememberMe, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });

        db.AuditLogs.Add(new Core.Models.AuditLog { UserId = user.Id, ActionCode = "LoginSuccess", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        if (user.MustChangePassword) return RedirectToAction(nameof(ChangePassword), new { returnUrl = model.ReturnUrl });
        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl)) return Redirect(model.ReturnUrl);
        return RedirectToAction("Index", "Requests");
    }

    [Authorize]
    public async Task<IActionResult> Logout()
    {
        db.AuditLogs.Add(new Core.Models.AuditLog { ActionCode = "Logout", DetailsJson = User.Identity?.Name, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    [Authorize]
    [HttpGet]
    public IActionResult ChangePassword(string? returnUrl = null) => View(new ChangePasswordViewModel { ReturnUrl = returnUrl });

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await db.AppUsers.SingleAsync(u => u.UserName == User.Identity!.Name);
        if (user.PasswordHash is null || user.PasswordSalt is null || !PasswordHasher.Verify(model.CurrentPassword, user.PasswordHash, user.PasswordSalt))
        {
            ModelState.AddModelError(nameof(model.CurrentPassword), "Current password is incorrect.");
            return View(model);
        }

        var (hash, salt) = PasswordHasher.Hash(model.NewPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.MustChangePassword = false;
        db.AuditLogs.Add(new Core.Models.AuditLog { UserId = user.Id, ActionCode = "PasswordChanged", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        TempData["Success"] = "Password updated.";
        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl)) return Redirect(model.ReturnUrl);
        return RedirectToAction("Index", "Requests");
    }
}
