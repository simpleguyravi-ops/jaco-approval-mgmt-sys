using System.Security.Cryptography;
using System.Text.Json;
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
        // Shown exactly once, right after a bulk import creates new accounts -- same
        // one-time-reveal lifetime as a freshly issued API key (TempData survives one
        // redirect), since a generated password is just as much a secret as one.
        if (TempData["NewUserCredentialsJson"] is string json)
            ViewBag.NewUserCredentials = JsonSerializer.Deserialize<List<UserCredentialReveal>>(json);

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

    // ---------- Bulk CSV import (accounts + per-Approval-Type Create/View access) ----------
    // One row = one user plus their access grant for every currently active Approval Type,
    // so onboarding a batch of new starters is one file instead of one UserAccounts visit
    // (to create the login) plus one UsersRoles visit per Approval Type (to grant access).
    static readonly string[] BaseImportHeader = ["UserName", "DisplayName", "Department", "Branch", "Email", "IsAdmin", "IsAuditor", "Active"];

    [HttpGet]
    public IActionResult Import() => View();

    [HttpGet]
    public async Task<IActionResult> ImportTemplate()
    {
        var codes = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name).Select(t => t.Code).ToListAsync();
        var header = BaseImportHeader.Concat(codes.SelectMany(c => new[] { $"{c}_CanCreate", $"{c}_CanView" }));
        var sample = string.Join(",", header) + "\n" +
            "jsmith,John Smith,IT,,jsmith@example.com,N,N,Y" + string.Concat(codes.Select(_ => ",Y,Y")) + "\n";
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sample)).ToArray();
        return File(bytes, "text/csv", "user-accounts-template.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> ImportPreview(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose a CSV file first.";
            return RedirectToAction(nameof(Import));
        }

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8))
            content = await reader.ReadToEndAsync();

        var preview = await BuildUserPreviewAsync(content);
        preview.EncodedFile = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        preview.FileName = file.FileName;

        return View("ImportPreview", preview);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportConfirm(string encodedFile)
    {
        string content;
        try { content = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedFile)); }
        catch
        {
            TempData["Error"] = "The uploaded file could not be re-read. Please upload it again.";
            return RedirectToAction(nameof(Import));
        }

        // Never trust the hidden round-tripped field blindly -- re-validate exactly as the
        // preview step did before writing anything.
        var preview = await BuildUserPreviewAsync(content);
        if (preview.ErrorCount > 0)
        {
            TempData["Error"] = $"{preview.ErrorCount} row(s) still have errors -- fix the file and re-upload. Nothing was changed.";
            preview.EncodedFile = encodedFile;
            return View("ImportPreview", preview);
        }

        var created = new List<UserCredentialReveal>();
        var updatedCount = 0;

        foreach (var row in preview.Rows)
        {
            var user = await db.AppUsers.SingleOrDefaultAsync(u => u.UserName == row.UserName);
            var isNew = user is null;
            string? password = null;

            if (isNew)
            {
                // Never accept a password through the CSV itself -- a spreadsheet full of
                // plaintext passwords sitting on someone's disk is exactly the kind of
                // exposure worth designing out, not just discouraging.
                password = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 12);
                var (hash, salt) = PasswordHasher.Hash(password);
                user = new AppUser { UserName = row.UserName, PasswordHash = hash, PasswordSalt = salt, MustChangePassword = true };
                db.AppUsers.Add(user);
            }

            user!.DisplayName = row.DisplayName;
            user.Department = row.Department;
            user.Branch = row.Branch;
            user.Email = row.Email;
            user.IsAdmin = row.IsAdmin;
            user.IsAuditor = row.IsAuditor;
            user.IsActive = row.Active;
            await db.SaveChangesAsync(); // need user.Id for the permission rows below

            if (isNew) created.Add(new UserCredentialReveal { UserName = row.UserName, Password = password! });
            else updatedCount++;

            foreach (var (typeId, perms) in row.Permissions)
            {
                var permission = await db.UserWorkflowPermissions.SingleOrDefaultAsync(p => p.UserId == user.Id && p.ApprovalTypeId == typeId);
                if (permission is null)
                {
                    permission = new UserWorkflowPermission { UserId = user.Id, ApprovalTypeId = typeId };
                    db.UserWorkflowPermissions.Add(permission);
                }
                permission.CanCreate = perms.CanCreate;
                permission.CanView = perms.CanView;
            }

            db.AuditLogs.Add(new AuditLog { UserId = user.Id, ActionCode = isNew ? "UserAccountCreated" : "UserAccountUpdated", DetailsJson = $"{row.UserName} (bulk import)", CreatedAt = DateTime.UtcNow });
        }

        await db.SaveChangesAsync();

        if (created.Count > 0)
            TempData["NewUserCredentialsJson"] = JsonSerializer.Serialize(created);
        TempData["Success"] = $"Import complete -- {created.Count} account(s) created, {updatedCount} updated.";
        return RedirectToAction(nameof(Index));
    }

    async Task<UserImportPreview> BuildUserPreviewAsync(string csvContent)
    {
        var types = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name).Select(t => new { t.Id, t.Code, t.Name }).ToListAsync();
        var preview = new UserImportPreview { ApprovalTypes = types.Select(t => (t.Id, t.Code, t.Name)).ToList() };

        var table = CsvParser.Parse(csvContent);
        if (table.Count < 2)
        {
            preview.Rows.Add(new UserImportRow { RowNumber = 0, Errors = { "File has no data rows." } });
            return preview;
        }

        var header = table[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        string? Get(string[] cells, int idx) => idx >= 0 && idx < cells.Length && !string.IsNullOrWhiteSpace(cells[idx]) ? cells[idx].Trim() : null;
        bool GetBool(string[] cells, int idx)
        {
            var v = Get(cells, idx);
            return string.Equals(v, "Y", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "Yes", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        var iUserName = Col("UserName"); var iDisplayName = Col("DisplayName"); var iDept = Col("Department");
        var iBranch = Col("Branch"); var iEmail = Col("Email"); var iAdmin = Col("IsAdmin"); var iAuditor = Col("IsAuditor"); var iActive = Col("Active");

        if (iUserName < 0 || iDisplayName < 0)
        {
            preview.Rows.Add(new UserImportRow { RowNumber = 0, Errors = { $"Header is missing required columns. Expected at least: {string.Join(",", BaseImportHeader)}" } });
            return preview;
        }

        var typeColumns = types.Select(t => (t.Id, t.Name, CanCreateCol: Col($"{t.Code}_CanCreate"), CanViewCol: Col($"{t.Code}_CanView"))).ToList();
        var existingUserNames = await db.AppUsers.Select(u => u.UserName).ToListAsync();
        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < table.Count; r++)
        {
            var cells = table[r];
            if (cells.Length == 1 && string.IsNullOrWhiteSpace(cells[0])) continue; // trailing blank line

            var row = new UserImportRow
            {
                RowNumber = r + 1,
                UserName = Get(cells, iUserName) ?? "",
                DisplayName = Get(cells, iDisplayName) ?? "",
                Department = Get(cells, iDept),
                Branch = Get(cells, iBranch),
                Email = Get(cells, iEmail),
                IsAdmin = GetBool(cells, iAdmin),
                IsAuditor = GetBool(cells, iAuditor),
                Active = Get(cells, iActive) is null || GetBool(cells, iActive)
            };
            row.IsNewUser = !existingUserNames.Contains(row.UserName, StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(row.UserName)) row.Errors.Add("UserName is required.");
            if (string.IsNullOrWhiteSpace(row.DisplayName)) row.Errors.Add("DisplayName is required.");
            if (!string.IsNullOrWhiteSpace(row.UserName) && !seenInFile.Add(row.UserName))
                row.Errors.Add("Duplicate UserName within this file.");
            if (!string.IsNullOrWhiteSpace(row.Email) && !row.Email.Contains('@'))
                row.Errors.Add("Email doesn't look like a valid address.");

            var summaryParts = new List<string>();
            foreach (var (typeId, typeName, canCreateCol, canViewCol) in typeColumns)
            {
                if (canCreateCol < 0 && canViewCol < 0) continue;
                var canCreate = GetBool(cells, canCreateCol);
                var canView = GetBool(cells, canViewCol);
                if (!canCreate && !canView) continue;
                row.Permissions[typeId] = (canCreate, canView);
                var caps = string.Join("+", new[] { canCreate ? "Create" : null, canView ? "View" : null }.Where(s => s is not null));
                summaryParts.Add($"{typeName}: {caps}");
            }
            row.PermissionsSummary = string.Join(", ", summaryParts);

            preview.Rows.Add(row);
        }

        return preview;
    }
}
