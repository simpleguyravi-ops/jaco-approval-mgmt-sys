using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// CanCreate/CanView are the same two flags the original Approval engine used -- CanView is
// relabelled "Display All" here since that's what it actually grants: oversight into every
// request of a type, on top of the automatic creator/participant access nobody needs to
// grant explicitly (see RequestService.IsParticipantAsync).
[Authorize(Policy = "UnifiedAdmin")]
public sealed class UsersRolesController(UnifiedDbContext db, RequestService requests) : Controller
{
    public async Task<IActionResult> Index(int approvalTypeId)
    {
        // Kept in sync on every visit -- same as Rule Builder/Digest -- so a person who
        // signed in for the first time recently shows up here without a manual step.
        await requests.SyncUsersFromPortalAsync();

        var types = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name).ToListAsync();
        if (approvalTypeId == 0 && types.Count > 0) approvalTypeId = types[0].Id;

        ViewBag.Types = types;
        ViewBag.ApprovalTypeId = approvalTypeId;

        var users = await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync();
        var perms = await db.UserWorkflowPermissions.Where(p => p.ApprovalTypeId == approvalTypeId).ToDictionaryAsync(p => p.UserId);
        ViewBag.Permissions = perms;

        return View(users);
    }

    public async Task<IActionResult> Export()
    {
        var users = await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(users,
            ["Name", "Username", "Department"],
            u => [u.DisplayName, u.UserName, u.Department ?? ""]);
        return File(bytes, "text/csv", $"users-roles-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int approvalTypeId, [FromForm] List<int> canCreateUserIds, [FromForm] List<int> canViewUserIds)
    {
        var actingUser = await requests.ResolveCurrentUserAsync(User);

        var existing = await db.UserWorkflowPermissions.Where(p => p.ApprovalTypeId == approvalTypeId).ToListAsync();
        var before = existing.Select(p => new { p.UserId, p.CanCreate, p.CanView }).ToList();
        var affectedUserIds = canCreateUserIds.Concat(canViewUserIds).Concat(existing.Select(p => p.UserId)).Distinct();

        foreach (var userId in affectedUserIds)
        {
            var perm = existing.SingleOrDefault(p => p.UserId == userId);
            var canCreate = canCreateUserIds.Contains(userId);
            var canView = canViewUserIds.Contains(userId);

            if (perm is null)
            {
                if (canCreate || canView)
                    db.UserWorkflowPermissions.Add(new UserWorkflowPermission { UserId = userId, ApprovalTypeId = approvalTypeId, CanCreate = canCreate, CanView = canView });
            }
            else
            {
                perm.CanCreate = canCreate;
                perm.CanView = canView;
            }
        }

        // Granting/revoking access to an Approval Type is itself an access-control change,
        // so it belongs in the audit trail like any other state change -- not tied to one
        // RequestId since it isn't about a single workflow item.
        db.AuditLogs.Add(new AuditLog
        {
            RequestId = null,
            UserId = actingUser?.Id,
            ActionCode = "PermissionChange",
            DetailsJson = JsonSerializer.Serialize(new
            {
                approvalTypeId,
                before,
                after = affectedUserIds.Select(userId => new { userId, canCreate = canCreateUserIds.Contains(userId), canView = canViewUserIds.Contains(userId) })
            }),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        TempData["Success"] = "Permissions saved.";
        return RedirectToAction(nameof(Index), new { approvalTypeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncFromPortal(int approvalTypeId)
    {
        await requests.SyncUsersFromPortalAsync();
        TempData["Success"] = "User roster synced from Portal.";
        return RedirectToAction(nameof(Index), new { approvalTypeId });
    }
}
