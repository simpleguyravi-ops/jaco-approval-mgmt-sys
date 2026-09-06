using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// A Task Type's own identity + lifecycle -- its fields are configured separately, on
// WorkflowFieldsController (same catalog Approval Types use, scoped by TaskTypeId instead
// of ApprovalTypeId), same split as ApprovalTypesController/WorkflowFieldsController today.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class TaskTypesController(UnifiedDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? sort, string dir = "asc")
    {
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var desc = dir == "desc";
        IQueryable<TaskType> query = db.TaskTypes;
        query = sort switch
        {
            "Code" => desc ? query.OrderByDescending(t => t.Code) : query.OrderBy(t => t.Code),
            "Status" => desc ? query.OrderByDescending(t => t.Active) : query.OrderBy(t => t.Active),
            _ => query.OrderBy(t => t.Name)
        };
        return View(await query.ToListAsync());
    }

    [HttpGet]
    public IActionResult Create() => View(new TaskType { Active = true });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TaskType model)
    {
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        model.Name = (model.Name ?? "").Trim();

        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name))
        {
            TempData["Error"] = "Code and Name are required.";
            return View(model);
        }
        if (await db.TaskTypes.AnyAsync(t => t.Code == model.Code))
        {
            TempData["Error"] = "That Code is already in use.";
            return View(model);
        }

        db.TaskTypes.Add(model);
        await db.SaveChangesAsync();

        TempData["Success"] = $"'{model.Name}' created. Now add its fields under Criteria Fields.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var type = await db.TaskTypes.FindAsync(id);
        return type is null ? NotFound() : View(type);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, TaskType model)
    {
        var type = await db.TaskTypes.FindAsync(id);
        if (type is null) return NotFound();

        var code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(model.Name))
        {
            TempData["Error"] = "Code and Name are required.";
            return View(type);
        }
        if (await db.TaskTypes.AnyAsync(t => t.Code == code && t.Id != id))
        {
            TempData["Error"] = "That Code is already in use by another Task Type.";
            return View(type);
        }

        type.Code = code;
        type.Name = model.Name;
        type.Active = model.Active;
        await db.SaveChangesAsync();

        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Delete(int id)
    {
        var type = await db.TaskTypes.FindAsync(id);
        if (type is null) return NotFound();

        var ruleIds = await FindAssignTaskRuleIdsAsync(id);

        return View(new TaskTypeDeleteViewModel
        {
            Id = type.Id,
            Name = type.Name,
            OpenTaskCount = await db.AssignedTasks.CountAsync(t => t.TaskTypeId == id && t.Status != "Done"),
            ClosedTaskCount = await db.AssignedTasks.CountAsync(t => t.TaskTypeId == id && t.Status == "Done"),
            WorkflowFieldCount = await db.WorkflowFields.CountAsync(f => f.TaskTypeId == id),
            PostProcessingRuleCount = ruleIds.Count
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var type = await db.TaskTypes.FindAsync(id);
        if (type is null) return NotFound();

        // Re-checked here, not just in the view -- a POST can be replayed/forged independent
        // of what the confirm page showed, including after a task was assigned in the time
        // between loading that page and clicking Delete.
        var openTaskCount = await db.AssignedTasks.CountAsync(t => t.TaskTypeId == id && t.Status != "Done");
        if (openTaskCount > 0)
        {
            TempData["Error"] = $"Can't delete '{type.Name}' -- {openTaskCount} task(s) of this type are still open. Complete them first, then come back here.";
            return RedirectToAction(nameof(Delete), new { id });
        }

        var workflowFields = await db.WorkflowFields.Where(f => f.TaskTypeId == id).ToListAsync();
        var closedTasks = await db.AssignedTasks.Where(t => t.TaskTypeId == id).ToListAsync(); // all Done, per the check above
        var ruleIds = await FindAssignTaskRuleIdsAsync(id);
        var ruleLinks = await db.PostProcessingRuleApprovalTypes.Where(x => ruleIds.Contains(x.PostProcessingRuleId)).ToListAsync();
        var ruleCriteria = await db.PostProcessingRuleCriteria.Where(c => ruleIds.Contains(c.PostProcessingRuleId)).ToListAsync();
        var rules = await db.PostProcessingRules.Where(r => ruleIds.Contains(r.Id)).ToListAsync();

        // One recovery snapshot of everything this Task Type owned, same shape as Approval
        // Type deletion's own archive -- restorable from Archived Clears. LogType is
        // NVARCHAR(50) -- truncate rather than let a long Task Type name overflow it.
        var logType = $"TaskTypeDeleted:{type.Name}";
        if (logType.Length > 50) logType = logType[..50];
        db.LogArchives.Add(new LogArchive
        {
            LogType = logType,
            BeforeDate = DateTime.UtcNow,
            EntryCount = closedTasks.Count,
            ContentJson = JsonSerializer.Serialize(new { taskType = type, workflowFields, closedTasks, rules, ruleLinks, ruleCriteria }),
            ClearedByUserName = User.Identity?.Name,
            ClearedAt = DateTime.UtcNow
        });

        db.WorkflowFields.RemoveRange(workflowFields);
        db.AssignedTasks.RemoveRange(closedTasks);
        // An AssignTask rule names exactly one Task Type (unlike an Approval Type, which a
        // PPF rule can share across several) -- so every rule found here is deleted outright,
        // not just unlinked.
        db.PostProcessingRuleApprovalTypes.RemoveRange(ruleLinks);
        db.PostProcessingRuleCriteria.RemoveRange(ruleCriteria);
        db.PostProcessingRules.RemoveRange(rules);
        db.TaskTypes.Remove(type);

        db.AuditLogs.Add(new AuditLog
        {
            ActionCode = "TaskTypeDeleted",
            DetailsJson = JsonSerializer.Serialize(new
            {
                taskType = type.Name,
                code = type.Code,
                tasksDeleted = closedTasks.Count,
                workflowFieldsDeleted = workflowFields.Count,
                postProcessingRulesDeleted = rules.Count,
                deletedBy = User.Identity?.Name
            }),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        TempData["Success"] = $"'{type.Name}' and everything attached to it (archived first -- see Archived Clears) have been permanently deleted.";
        return RedirectToAction(nameof(Index));
    }

    // taskTypeId lives inside ActionConfigJson (a plain int property), not a modeled
    // relation, since AssignTask config is one JSON blob like every other action type here --
    // so finding "which rules target this Task Type" means parsing that JSON client-side.
    async Task<List<int>> FindAssignTaskRuleIdsAsync(int taskTypeId)
    {
        var rules = await db.PostProcessingRules.Where(r => r.ActionType == "AssignTask").ToListAsync();
        return rules.Where(r =>
        {
            try
            {
                using var doc = JsonDocument.Parse(r.ActionConfigJson ?? "{}");
                return doc.RootElement.TryGetProperty("taskTypeId", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == taskTypeId;
            }
            catch { return false; }
        }).Select(r => r.Id).ToList();
    }
}
