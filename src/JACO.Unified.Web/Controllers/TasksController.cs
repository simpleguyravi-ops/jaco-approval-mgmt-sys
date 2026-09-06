using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// "My Tasks" -- work items an "Assign a task" PPF rule created, either for a specific user
// or for a whole department queue (whoever completes it first claims it). Deliberately
// shows only the Task Type's own fields plus bare identifying context (Request Number/
// Subject/Type/Status) and whichever Request fields the rule's admin explicitly opted in --
// never the full Request.DataJson. See PpfRuleEditViewModel.TaskContextFieldKeys.
public sealed class TasksController(UnifiedDbContext db, NotificationQueue notifications, RequestService requests) : UnifiedControllerBase(requests)
{
    public async Task<IActionResult> Index(string? sort, string dir = "asc")
    {
        var me = await CurrentUserAsync();
        var tasks = await db.AssignedTasks
            .Where(t => t.AssignedToUserId == me.Id || (t.AssignedToUserId == null && t.AssignedToDepartment == me.Department))
            .ToListAsync();

        var requestIds = tasks.Select(t => t.RequestId).Distinct().ToList();
        var requestInfo = await db.Requests.Where(r => requestIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => (r.RequestNumber, r.ApprovalTypeId));
        var typeNames = await db.ApprovalTypes.ToDictionaryAsync(t => t.Id, t => t.Name);
        var taskTypeNames = await db.TaskTypes.ToDictionaryAsync(t => t.Id, t => t.Name);

        var rows = tasks.Select(t =>
        {
            var (number, typeId) = requestInfo.GetValueOrDefault(t.RequestId, ("(deleted)", 0));
            return new TaskListRow
            {
                Id = t.Id,
                RequestId = t.RequestId,
                RequestNumber = number,
                ApprovalTypeName = typeNames.GetValueOrDefault(typeId, "(unknown)"),
                Title = t.Title,
                TaskTypeName = taskTypeNames.GetValueOrDefault(t.TaskTypeId, "(unknown)"),
                AssignedLabel = t.AssignedToUserId.HasValue ? "You" : $"{t.AssignedToDepartment} (queue)",
                Status = t.Status,
                CreatedAtUtc = t.CreatedAtUtc,
                DueAtUtc = t.DueAtUtc
            };
        }).ToList();

        var desc = dir == "desc";
        IOrderedEnumerable<TaskListRow>? ordered = sort switch
        {
            "Request" => desc ? rows.OrderByDescending(r => r.RequestNumber) : rows.OrderBy(r => r.RequestNumber),
            "Title" => desc ? rows.OrderByDescending(r => r.Title) : rows.OrderBy(r => r.Title),
            "Due" => desc ? rows.OrderByDescending(r => r.DueAtUtc) : rows.OrderBy(r => r.DueAtUtc),
            "Status" => desc ? rows.OrderByDescending(r => r.Status) : rows.OrderBy(r => r.Status),
            _ => null
        };
        // Default: open work first, soonest due date first -- what an assignee actually
        // wants to see at the top without having to sort for it.
        rows = ordered?.ToList() ?? rows.OrderBy(r => r.Status == "Done").ThenBy(r => r.DueAtUtc ?? DateTime.MaxValue).ToList();

        return View(new TaskListViewModel
        {
            Rows = rows,
            OpenCount = rows.Count(r => r.Status == "Open"),
            DoneCount = rows.Count(r => r.Status == "Done"),
            Sort = sort,
            Dir = dir
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id)
    {
        var me = await CurrentUserAsync();
        var task = await db.AssignedTasks.FindAsync(id);
        if (task is null || !CanAccess(task, me)) return NotFound();

        var request = await db.Requests.FindAsync(task.RequestId);
        var type = request is null ? null : await db.ApprovalTypes.FindAsync(request.ApprovalTypeId);
        var fields = await db.WorkflowFields.Where(f => f.TaskTypeId == task.TaskTypeId && f.Active).OrderBy(f => f.DisplayOrder).ToListAsync();
        var currentValues = string.IsNullOrWhiteSpace(task.DataJson) ? new Dictionary<string, JsonElement>() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(task.DataJson) ?? new();
        var values = fields.ToDictionary(f => f.FieldKey, f => currentValues.TryGetValue(f.FieldKey, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null);
        var picklists = new Dictionary<string, List<PicklistValue>>();
        foreach (var lookupType in fields.Where(f => f.DataType == FieldDataType.Dropdown && !string.IsNullOrEmpty(f.LookupType)).Select(f => f.LookupType!).Distinct())
            picklists[lookupType] = await requests.GetPicklistAsync(lookupType);

        var contextFields = new List<(string Label, string? Value)>();
        if (request is not null)
        {
            var rule = await db.PostProcessingRules.FindAsync(task.PostProcessingRuleId);
            var contextKeys = ExtractContextFieldKeys(rule?.ActionConfigJson);
            if (contextKeys.Count > 0)
            {
                var contextFieldDefs = await db.WorkflowFields.Where(f => contextKeys.Contains(f.FieldKey) && f.TaskTypeId == null
                    && (f.ApprovalTypeId == request.ApprovalTypeId || f.ApprovalTypeId == null)).ToListAsync();
                foreach (var key in contextKeys)
                {
                    var def = contextFieldDefs.FirstOrDefault(f => f.FieldKey == key);
                    if (def is null) continue;
                    contextFields.Add((def.FieldLabel, RequestService.ExtractField(request.DataJson, key)));
                }
            }
        }

        var completedByName = task.CompletedByUserId.HasValue ? (await db.AppUsers.FindAsync(task.CompletedByUserId.Value))?.DisplayName : null;

        return View(new TaskCompletionViewModel
        {
            Id = task.Id,
            RequestId = task.RequestId,
            RequestNumber = request?.RequestNumber ?? "(deleted request)",
            ApprovalTypeName = type?.Name ?? "(unknown)",
            Title = task.Title,
            Status = task.Status,
            Fields = fields,
            Values = values,
            Picklists = picklists,
            ContextFields = contextFields,
            DueAtUtc = task.DueAtUtc,
            CompletedAtUtc = task.CompletedAtUtc,
            CompletedByName = completedByName
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(long id)
    {
        var me = await CurrentUserAsync();
        var task = await db.AssignedTasks.FindAsync(id);
        if (task is null || !CanAccess(task, me)) return NotFound();
        if (task.Status == "Done")
        {
            TempData["Error"] = "This task was already completed.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var fields = await db.WorkflowFields.Where(f => f.TaskTypeId == task.TaskTypeId && f.Active).ToListAsync();
        var values = new Dictionary<string, string?>();
        foreach (var f in fields)
        {
            var raw = Request.Form[$"Fields[{f.FieldKey}]"].ToString();
            if (f.IsRequired && string.IsNullOrWhiteSpace(raw))
            {
                TempData["Error"] = $"{f.FieldLabel} is required.";
                return RedirectToAction(nameof(Details), new { id });
            }
            values[f.FieldKey] = raw;
        }

        task.DataJson = JsonSerializer.Serialize(values);
        task.Status = "Done";
        task.CompletedAtUtc = DateTime.UtcNow;
        task.CompletedByUserId = me.Id;
        // Stop the overdue scan from ever touching this task again.
        task.NextOverdueCheckAtUtc = null;
        await db.SaveChangesAsync();

        notifications.Enqueue(task.RequestId, "TaskCompleted", me.Id);

        TempData["Success"] = "Task completed.";
        return RedirectToAction(nameof(Index));
    }

    bool CanAccess(AssignedTask task, AppUser me) =>
        task.AssignedToUserId == me.Id
        || (task.AssignedToUserId is null && task.AssignedToDepartment is not null && task.AssignedToDepartment == me.Department)
        || IsAdmin;

    static List<string> ExtractContextFieldKeys(string? actionConfigJson)
    {
        if (string.IsNullOrWhiteSpace(actionConfigJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(actionConfigJson);
            if (doc.RootElement.TryGetProperty("contextFieldKeys", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch { /* malformed config -- no context fields shown */ }
        return [];
    }
}
