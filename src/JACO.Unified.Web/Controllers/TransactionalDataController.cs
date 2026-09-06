using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Wipes every real Request (and everything hung off it) for one Approval Type -- for
// resetting a test/UAT Approval Type back to empty between rounds, not for production
// cleanup. Gated on SystemSettings.IsProduction (see SystemSettingsController): the whole
// feature is a no-op while the system is marked Production, checked both here and again
// on the confirm POST, so a stale page or a replayed request can't slip through after
// someone flips the mode.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class TransactionalDataController(UnifiedDbContext db, RequestAttachmentStorage attachmentStorage) : Controller
{
    async Task<bool> IsProductionAsync() =>
        (await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1))?.IsProduction ?? false;

    async Task<List<long>> RequestIdsForTypeAsync(int approvalTypeId) =>
        await db.Requests.Where(r => r.ApprovalTypeId == approvalTypeId).Select(r => r.Id).ToListAsync();

    async Task<TransactionalDataCounts> CountsForAsync(List<long> requestIds) => new()
    {
        Requests = requestIds.Count,
        Attachments = await db.RequestAttachments.CountAsync(a => requestIds.Contains(a.RequestId)),
        Actions = await db.RequestActions.CountAsync(a => requestIds.Contains(a.RequestId)),
        PpfExecutions = await db.PostProcessingExecutions.CountAsync(e => requestIds.Contains(e.RequestId)),
        Reassignments = await db.ApproverReassignments.CountAsync(r => requestIds.Contains(r.RequestId)),
        Participants = await db.WorkflowParticipants.CountAsync(p => requestIds.Contains(p.RequestId)),
    };

    [HttpGet]
    public async Task<IActionResult> Index(int? approvalTypeId)
    {
        var types = await db.ApprovalTypes.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();
        var model = new TransactionalDataClearViewModel
        {
            IsProduction = await IsProductionAsync(),
            ApprovalTypes = types.Select(t => (t.Id, t.Name)).ToList(),
            ApprovalTypeId = approvalTypeId
        };
        if (model.IsProduction || approvalTypeId is null) return View(model);

        var type = await db.ApprovalTypes.FindAsync(approvalTypeId.Value);
        if (type is null) return View(model);

        model.ApprovalTypeName = type.Name;
        model.Counts = await CountsForAsync(await RequestIdsForTypeAsync(approvalTypeId.Value));
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearConfirmed(int approvalTypeId)
    {
        // Re-checked here, not just in the view -- a POST can be replayed/forged independent
        // of what the confirm page showed, including after someone flips the mode to
        // Production in a different tab.
        if (await IsProductionAsync())
        {
            TempData["Error"] = "System is in Production mode -- clearing transactional data is disabled.";
            return RedirectToAction(nameof(Index));
        }

        var type = await db.ApprovalTypes.FindAsync(approvalTypeId);
        if (type is null)
        {
            TempData["Error"] = "Approval Type not found.";
            return RedirectToAction(nameof(Index));
        }

        var requestIds = await RequestIdsForTypeAsync(approvalTypeId);
        if (requestIds.Count == 0)
        {
            TempData["Success"] = $"Nothing to clear -- {type.Name} has no requests.";
            return RedirectToAction(nameof(Index));
        }

        var attachments = await db.RequestAttachments.Where(a => requestIds.Contains(a.RequestId)).ToListAsync();
        var actions = await db.RequestActions.Where(a => requestIds.Contains(a.RequestId)).ToListAsync();
        var ppfExecutions = await db.PostProcessingExecutions.Where(e => requestIds.Contains(e.RequestId)).ToListAsync();
        var reassignments = await db.ApproverReassignments.Where(r => requestIds.Contains(r.RequestId)).ToListAsync();
        var participants = await db.WorkflowParticipants.Where(p => requestIds.Contains(p.RequestId)).ToListAsync();
        var requests = await db.Requests.Where(r => requestIds.Contains(r.Id)).ToListAsync();

        // One recovery snapshot for the whole operation, same shape as CockpitController's
        // Archive<T> for logs -- restorable from Archived Clears if the wrong type gets
        // picked, short of the attached files themselves (those are gone for good; the
        // confirm page says so).
        db.LogArchives.Add(new LogArchive
        {
            LogType = $"TransactionalData:{type.Name}",
            BeforeDate = DateTime.UtcNow,
            EntryCount = requests.Count,
            ContentJson = JsonSerializer.Serialize(new { requests, attachments, actions, ppfExecutions, reassignments, participants }),
            ClearedByUserName = User.Identity?.Name,
            ClearedAt = DateTime.UtcNow
        });

        db.RequestAttachments.RemoveRange(attachments);
        db.RequestActions.RemoveRange(actions);
        db.PostProcessingExecutions.RemoveRange(ppfExecutions);
        db.ApproverReassignments.RemoveRange(reassignments);
        db.WorkflowParticipants.RemoveRange(participants);
        db.Requests.RemoveRange(requests);

        // AuditLogs for these requests are deliberately left in place (with a now-dangling
        // RequestId) rather than deleted -- the audit trail is evidence that this data
        // existed and was cleared, not itself "transactional data" to wipe. The Audit Log
        // view already tolerates a missing Request (LEFT JOIN) and just shows a blank
        // Request No. for these older rows.
        db.AuditLogs.Add(new AuditLog
        {
            ActionCode = "TransactionalDataCleared",
            DetailsJson = JsonSerializer.Serialize(new
            {
                approvalType = type.Name,
                requestsDeleted = requests.Count,
                attachmentsDeleted = attachments.Count,
                actionsDeleted = actions.Count,
                clearedBy = User.Identity?.Name
            }),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        // Files live on disk, outside the transaction above -- deleted only after the DB
        // commit succeeds, so a failed save never leaves the DB thinking data survived
        // when its files are already gone.
        foreach (var id in requestIds)
        {
            try
            {
                var dir = attachmentStorage.GetRequestDirectory(id);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* one request's files failing to delete shouldn't block the rest */ }
        }

        TempData["Success"] = $"Cleared {requests.Count} {type.Name} request(s) and everything attached to them (archived first -- see Archived Clears). Uploaded files were permanently deleted.";
        return RedirectToAction(nameof(Index));
    }
}
