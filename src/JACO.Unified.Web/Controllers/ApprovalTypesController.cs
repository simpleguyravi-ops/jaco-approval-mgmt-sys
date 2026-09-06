using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Adding a brand new type of request to the whole platform: name/code here, then its
// field catalog (WorkflowFieldsController) and routing (RoutingRulesController) are
// configured separately -- this screen only owns the type's identity + lifecycle.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class ApprovalTypesController(UnifiedDbContext db, RequestAttachmentStorage attachmentStorage) : Controller
{
    // A request still waiting on a decision -- deleting the type out from under it would
    // strand whoever's supposed to act on it (or the creator, for a Draft) with no way
    // back in. Approved/Rejected/Withdrawn are the only statuses treated as resolved;
    // anything else (including a status added later) counts as open, not closed, to keep
    // this check safe by default rather than needing to recognize every "closed" name.
    static readonly string[] TerminalStatuses = ["Approved", "Rejected", "Withdrawn"];
    public async Task<IActionResult> Index(string? sort, string dir = "asc")
    {
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var desc = dir == "desc";
        IQueryable<ApprovalType> query = db.ApprovalTypes;
        query = sort switch
        {
            "Code" => desc ? query.OrderByDescending(t => t.Code) : query.OrderBy(t => t.Code),
            "Status" => desc ? query.OrderByDescending(t => t.Active) : query.OrderBy(t => t.Active),
            _ => query.OrderBy(t => t.Name)
        };
        return View(await query.ToListAsync());
    }

    public async Task<IActionResult> Export()
    {
        var types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(types,
            ["Code", "Name", "Status"],
            t => [t.Code, t.Name, t.Active ? "Active" : "Disabled"]);
        return File(bytes, "text/csv", $"approval-types-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet]
    public IActionResult Create() => View(new ApprovalType { Active = true });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApprovalType model)
    {
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        model.Name = (model.Name ?? "").Trim();

        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name))
        {
            TempData["Error"] = "Code and Name are required.";
            return View(model);
        }
        if (model.Code.Length > 3)
        {
            TempData["Error"] = "Code must be 3 characters or fewer -- it becomes the prefix on every request number of this type.";
            return View(model);
        }
        if (await db.ApprovalTypes.AnyAsync(t => t.Code == model.Code))
        {
            TempData["Error"] = "That Code is already in use.";
            return View(model);
        }

        db.ApprovalTypes.Add(model);
        await db.SaveChangesAsync();

        // Every type needs at least one current WorkflowVersion -- routing/steps hang off
        // it, and RoutingService.ResolveAsync has nothing to resolve without one.
        db.WorkflowVersions.Add(new WorkflowVersion { ApprovalTypeId = model.Id, VersionNo = 1, IsCurrent = true });
        await db.SaveChangesAsync();

        TempData["Success"] = $"'{model.Name}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var type = await db.ApprovalTypes.FindAsync(id);
        return type is null ? NotFound() : View(type);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ApprovalType model)
    {
        var type = await db.ApprovalTypes.FindAsync(id);
        if (type is null) return NotFound();

        var code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(model.Name))
        {
            TempData["Error"] = "Code and Name are required.";
            return View(type);
        }
        if (code.Length > 3)
        {
            TempData["Error"] = "Code must be 3 characters or fewer -- it becomes the prefix on every request number of this type.";
            return View(type);
        }
        if (await db.ApprovalTypes.AnyAsync(t => t.Code == code && t.Id != id))
        {
            TempData["Error"] = "That Code is already in use by another Approval Type.";
            return View(type);
        }

        type.Code = code;
        type.Name = model.Name;
        type.Description = model.Description;
        type.Active = model.Active;
        await db.SaveChangesAsync();

        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Delete(int id)
    {
        var type = await db.ApprovalTypes.FindAsync(id);
        if (type is null) return NotFound();

        var requestIds = await db.Requests.Where(r => r.ApprovalTypeId == id).Select(r => r.Id).ToListAsync();
        var openCount = await db.Requests.CountAsync(r => r.ApprovalTypeId == id && !TerminalStatuses.Contains(r.Status));
        var closedIds = requestIds; // only ever reaches the counts below once openCount == 0 (see view)

        var model = new ApprovalTypeDeleteViewModel
        {
            Id = type.Id,
            Name = type.Name,
            OpenRequestCount = openCount,
            ClosedRequests = new TransactionalDataCounts
            {
                Requests = closedIds.Count,
                Attachments = await db.RequestAttachments.CountAsync(a => closedIds.Contains(a.RequestId)),
                Actions = await db.RequestActions.CountAsync(a => closedIds.Contains(a.RequestId)),
                PpfExecutions = await db.PostProcessingExecutions.CountAsync(e => closedIds.Contains(e.RequestId)),
                Reassignments = await db.ApproverReassignments.CountAsync(r => closedIds.Contains(r.RequestId)),
                Participants = await db.WorkflowParticipants.CountAsync(p => closedIds.Contains(p.RequestId)),
            },
            WorkflowFieldCount = await db.WorkflowFields.CountAsync(f => f.ApprovalTypeId == id),
            RoutingRuleCount = await db.RoutingRules.CountAsync(r => db.WorkflowVersions.Where(v => v.ApprovalTypeId == id).Select(v => v.Id).Contains(r.WorkflowVersionId)),
            // A rule linked to this type AND another survives deletion (just loses this
            // one association) -- this count is still every rule that references the type
            // at all, for the admin's awareness, not just the ones that would be removed.
            PostProcessingRuleCount = await db.PostProcessingRuleApprovalTypes.CountAsync(x => x.ApprovalTypeId == id),
            UserPermissionCount = await db.UserWorkflowPermissions.CountAsync(p => p.ApprovalTypeId == id),
            DigestScheduleCount = await db.DigestSchedules.CountAsync(d => d.ApprovalTypeId == id),
            RoutingLogCount = await db.RoutingLog.CountAsync(r => r.ApprovalTypeId == id),
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var type = await db.ApprovalTypes.FindAsync(id);
        if (type is null) return NotFound();

        // Re-checked here, not just in the view -- a POST can be replayed/forged independent
        // of what the confirm page showed, including after a new request was submitted in
        // the time between loading that page and clicking Delete.
        var openCount = await db.Requests.CountAsync(r => r.ApprovalTypeId == id && !TerminalStatuses.Contains(r.Status));
        if (openCount > 0)
        {
            TempData["Error"] = $"Can't delete '{type.Name}' -- {openCount} request(s) are still open (Draft, Pending, or Sent Back). Resolve or withdraw them first.";
            return RedirectToAction(nameof(Delete), new { id });
        }

        var requestIds = await db.Requests.Where(r => r.ApprovalTypeId == id).Select(r => r.Id).ToListAsync();
        var attachments = await db.RequestAttachments.Where(a => requestIds.Contains(a.RequestId)).ToListAsync();
        var actions = await db.RequestActions.Where(a => requestIds.Contains(a.RequestId)).ToListAsync();
        var ppfExecutions = await db.PostProcessingExecutions.Where(e => requestIds.Contains(e.RequestId)).ToListAsync();
        var reassignments = await db.ApproverReassignments.Where(r => requestIds.Contains(r.RequestId)).ToListAsync();
        var participants = await db.WorkflowParticipants.Where(p => requestIds.Contains(p.RequestId)).ToListAsync();
        var requests = await db.Requests.Where(r => requestIds.Contains(r.Id)).ToListAsync();

        var workflowFields = await db.WorkflowFields.Where(f => f.ApprovalTypeId == id).ToListAsync();
        var workflowVersions = await db.WorkflowVersions.Where(v => v.ApprovalTypeId == id).ToListAsync();
        var versionIds = workflowVersions.Select(v => v.Id).ToList();
        var routingRules = await db.RoutingRules.Where(r => versionIds.Contains(r.WorkflowVersionId)).ToListAsync();
        var ruleIds = routingRules.Select(r => r.Id).ToList();
        var routingRuleCriteria = await db.RoutingRuleCriteria.Where(c => ruleIds.Contains(c.RoutingRuleId)).ToListAsync();
        var workflowSteps = await db.WorkflowSteps.Where(s => ruleIds.Contains(s.RoutingRuleId)).ToListAsync();
        var stepIds = workflowSteps.Select(s => s.Id).ToList();
        var workflowStepApprovers = await db.WorkflowStepApprovers.Where(a => stepIds.Contains(a.WorkflowStepId)).ToListAsync();

        // A PPF rule can now apply to several Approval Types at once -- one linked ONLY to
        // this type is fully deleted (rule + its criteria); one also linked to another type
        // just loses this one association and otherwise survives untouched.
        var ruleLinksForType = await db.PostProcessingRuleApprovalTypes.Where(x => x.ApprovalTypeId == id).ToListAsync();
        var linkedRuleIds = ruleLinksForType.Select(x => x.PostProcessingRuleId).Distinct().ToList();
        var ruleIdsWithOtherTypes = (await db.PostProcessingRuleApprovalTypes
            .Where(x => linkedRuleIds.Contains(x.PostProcessingRuleId) && x.ApprovalTypeId != id)
            .Select(x => x.PostProcessingRuleId)
            .ToListAsync()).ToHashSet();
        var ruleIdsToFullyDelete = linkedRuleIds.Where(rid => !ruleIdsWithOtherTypes.Contains(rid)).ToList();
        var postProcessingRules = await db.PostProcessingRules.Where(r => ruleIdsToFullyDelete.Contains(r.Id)).ToListAsync();
        var postProcessingRuleCriteria = await db.PostProcessingRuleCriteria.Where(c => ruleIdsToFullyDelete.Contains(c.PostProcessingRuleId)).ToListAsync();
        var userPermissions = await db.UserWorkflowPermissions.Where(p => p.ApprovalTypeId == id).ToListAsync();
        var digestSchedules = await db.DigestSchedules.Where(d => d.ApprovalTypeId == id).ToListAsync();
        var digestRuns = await db.DigestRuns.Where(r => r.ApprovalTypeId == id).ToListAsync();
        var digestRunIds = digestRuns.Select(r => r.Id).ToList();
        var digestRunRecipients = await db.DigestRunRecipients.Where(r => digestRunIds.Contains(r.DigestRunId)).ToListAsync();
        var routingLogEntries = await db.RoutingLog.Where(r => r.ApprovalTypeId == id).ToListAsync();

        // One recovery snapshot of literally everything this type owned -- same shape as
        // Clear Transactional Data's archive, just covering the type's configuration too
        // (fields, routing, PPF rules, permissions, digest schedule) since none of that
        // survives the type itself. Restorable from Archived Clears short of the files.
        db.LogArchives.Add(new LogArchive
        {
            LogType = $"ApprovalTypeDeleted:{type.Name}",
            BeforeDate = DateTime.UtcNow,
            EntryCount = requests.Count,
            ContentJson = JsonSerializer.Serialize(new
            {
                approvalType = type, requests, attachments, actions, ppfExecutions, reassignments, participants,
                workflowFields, workflowVersions, routingRules, routingRuleCriteria, workflowSteps, workflowStepApprovers,
                postProcessingRules, postProcessingRuleCriteria, ruleLinksForType, userPermissions, digestSchedules, digestRuns, digestRunRecipients, routingLogEntries
            }),
            ClearedByUserName = User.Identity?.Name,
            ClearedAt = DateTime.UtcNow
        });

        db.RequestAttachments.RemoveRange(attachments);
        db.RequestActions.RemoveRange(actions);
        db.PostProcessingExecutions.RemoveRange(ppfExecutions);
        db.ApproverReassignments.RemoveRange(reassignments);
        db.WorkflowParticipants.RemoveRange(participants);
        db.Requests.RemoveRange(requests);
        db.WorkflowStepApprovers.RemoveRange(workflowStepApprovers);
        db.WorkflowSteps.RemoveRange(workflowSteps);
        db.RoutingRuleCriteria.RemoveRange(routingRuleCriteria);
        db.RoutingRules.RemoveRange(routingRules);
        db.WorkflowVersions.RemoveRange(workflowVersions);
        db.WorkflowFields.RemoveRange(workflowFields);
        // Every link to this type goes regardless of whether the rule itself survives;
        // only the rules that had no OTHER type left are then also deleted outright.
        db.PostProcessingRuleApprovalTypes.RemoveRange(ruleLinksForType);
        db.PostProcessingRuleCriteria.RemoveRange(postProcessingRuleCriteria);
        db.PostProcessingRules.RemoveRange(postProcessingRules);
        db.UserWorkflowPermissions.RemoveRange(userPermissions);
        db.DigestRunRecipients.RemoveRange(digestRunRecipients);
        db.DigestRuns.RemoveRange(digestRuns);
        db.DigestSchedules.RemoveRange(digestSchedules);
        db.RoutingLog.RemoveRange(routingLogEntries);
        db.ApprovalTypes.Remove(type);

        // Audit Log entries (LoginSuccess, Approve/Reject decisions, admin overrides, etc.)
        // referencing these requests are deliberately left in place, same as Clear
        // Transactional Data -- they're evidence the type and its requests existed, not
        // part of what's being cleaned up.
        db.AuditLogs.Add(new AuditLog
        {
            ActionCode = "ApprovalTypeDeleted",
            DetailsJson = JsonSerializer.Serialize(new
            {
                approvalType = type.Name,
                code = type.Code,
                requestsDeleted = requests.Count,
                workflowFieldsDeleted = workflowFields.Count,
                routingRulesDeleted = routingRules.Count,
                postProcessingRulesDeleted = postProcessingRules.Count,
                deletedBy = User.Identity?.Name
            }),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        foreach (var reqId in requestIds)
        {
            try
            {
                var dir = attachmentStorage.GetRequestDirectory(reqId);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* one request's files failing to delete shouldn't block the rest */ }
        }

        TempData["Success"] = $"'{type.Name}' and everything attached to it (archived first -- see Archived Clears) have been permanently deleted.";
        return RedirectToAction(nameof(Index));
    }
}
