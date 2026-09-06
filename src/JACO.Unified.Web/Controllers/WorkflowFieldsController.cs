using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// The field catalog Rule Builder's criteria dropdown reads from, and the SAME catalog
// that drives the dynamic Create/Edit form and Details panel. A field scoped to one
// Approval Type only shows up for that type; a field with no Approval Type ("Generic" --
// id 0 here, null in the database) shows up for every type.
public static class GenericApprovalType
{
    public const int Id = 0;
    public const string Name = "Generic (All Approval Types)";
}

// THE config screen the whole architecture pivot hinged on: add a field here and it's
// immediately available on the Create/Edit form, the Details panel, and Rule Builder's
// criteria dropdown -- no code change, no redeploy. Also owns Task Type fields (a field
// belongs to an Approval Type OR a Task Type, never both) -- scope is `approvalTypeId` for
// the former, `taskTypeId` for the latter; whichever is non-zero wins.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class WorkflowFieldsController(UnifiedDbContext db) : Controller
{
    public async Task<IActionResult> Index(int approvalTypeId, int taskTypeId, string? sort, string dir = "asc")
    {
        ViewBag.Types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
        ViewBag.TaskTypes = await db.TaskTypes.OrderBy(t => t.Name).ToListAsync();
        ViewBag.SelectedTypeId = approvalTypeId;
        ViewBag.SelectedTaskTypeId = taskTypeId;
        ViewBag.Sort = sort; ViewBag.Dir = dir;

        IQueryable<WorkflowField> query = taskTypeId > 0
            ? db.WorkflowFields.Where(f => f.TaskTypeId == taskTypeId)
            : db.WorkflowFields.Where(f => f.ApprovalTypeId == (approvalTypeId == GenericApprovalType.Id ? null : approvalTypeId) && f.TaskTypeId == null);
        var desc = dir == "desc";
        query = sort switch
        {
            "FieldKey" => desc ? query.OrderByDescending(f => f.FieldKey) : query.OrderBy(f => f.FieldKey),
            "FieldLabel" => desc ? query.OrderByDescending(f => f.FieldLabel) : query.OrderBy(f => f.FieldLabel),
            "DataType" => desc ? query.OrderByDescending(f => f.DataType) : query.OrderBy(f => f.DataType),
            "IsRequired" => desc ? query.OrderByDescending(f => f.IsRequired) : query.OrderBy(f => f.IsRequired),
            _ => query.OrderBy(f => f.DisplayOrder)
        };

        return View(await query.ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> Create(int approvalTypeId, int taskTypeId)
    {
        ViewBag.Types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
        ViewBag.TaskTypes = await db.TaskTypes.OrderBy(t => t.Name).ToListAsync();
        ViewBag.TaskTypeId = taskTypeId;
        return View(new WorkflowField
        {
            ApprovalTypeId = taskTypeId > 0 ? null : (approvalTypeId == GenericApprovalType.Id ? null : approvalTypeId),
            TaskTypeId = taskTypeId > 0 ? taskTypeId : null,
            IsVisible = true,
            IncludeInApi = true,
            Active = true,
            DataType = FieldDataType.Text
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(WorkflowField model, int approvalTypeId, int taskTypeId)
    {
        model.ApprovalTypeId = taskTypeId > 0 ? null : (approvalTypeId == GenericApprovalType.Id ? null : approvalTypeId);
        model.TaskTypeId = taskTypeId > 0 ? taskTypeId : null;
        model.FieldKey = (model.FieldKey ?? "").Trim();
        model.FieldLabel = (model.FieldLabel ?? "").Trim();
        model.LookupType = string.IsNullOrWhiteSpace(model.LookupType) ? null : model.LookupType.Trim();

        async Task<IActionResult> Reject(string message)
        {
            TempData["Error"] = message;
            ViewBag.Types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
            ViewBag.TaskTypes = await db.TaskTypes.OrderBy(t => t.Name).ToListAsync();
            ViewBag.TaskTypeId = taskTypeId;
            return View(model);
        }

        if (string.IsNullOrWhiteSpace(model.FieldKey) || string.IsNullOrWhiteSpace(model.FieldLabel))
            return await Reject("Field Key and Label are required.");
        if (await db.WorkflowFields.AnyAsync(f => f.ApprovalTypeId == model.ApprovalTypeId && f.TaskTypeId == model.TaskTypeId && f.FieldKey == model.FieldKey))
            return await Reject("That Field Key already exists for this scope.");
        if (!string.IsNullOrEmpty(model.LookupType) && model.DataType != FieldDataType.Dropdown)
            return await Reject("Lookup Type only has an effect when Data Type is Dropdown -- set Data Type to Dropdown, or clear Lookup Type. Otherwise the field silently renders as a plain input.");
        if (model.DataType == FieldDataType.Dropdown && string.IsNullOrEmpty(model.LookupType))
            return await Reject("Data Type is Dropdown but no Lookup Type is set -- the field would show an empty list. Set a Lookup Type (matching a Picklist Values entry).");

        db.WorkflowFields.Add(model);
        await db.SaveChangesAsync();
        TempData["Success"] = $"Field '{model.FieldLabel}' added.";
        return RedirectToAction(nameof(Index), new { approvalTypeId, taskTypeId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var field = await db.WorkflowFields.FindAsync(id);
        if (field is null) return NotFound();
        ViewBag.ScopeName = await ScopeNameAsync(field);
        return View(field);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, WorkflowField model)
    {
        var field = await db.WorkflowFields.FindAsync(id);
        if (field is null) return NotFound();

        model.FieldLabel = (model.FieldLabel ?? "").Trim();
        model.LookupType = string.IsNullOrWhiteSpace(model.LookupType) ? null : model.LookupType.Trim();

        async Task<IActionResult> Reject(string message)
        {
            TempData["Error"] = message;
            model.Id = field.Id;
            ViewBag.ScopeName = await ScopeNameAsync(field);
            return View(model);
        }

        if (string.IsNullOrWhiteSpace(model.FieldLabel)) return await Reject("Label is required.");
        if (!string.IsNullOrEmpty(model.LookupType) && model.DataType != FieldDataType.Dropdown)
            return await Reject("Lookup Type only has an effect when Data Type is Dropdown -- set Data Type to Dropdown, or clear Lookup Type. Otherwise the field silently renders as a plain input.");
        if (model.DataType == FieldDataType.Dropdown && string.IsNullOrEmpty(model.LookupType))
            return await Reject("Data Type is Dropdown but no Lookup Type is set -- the field would show an empty list. Set a Lookup Type (matching a Picklist Values entry).");

        field.FieldLabel = model.FieldLabel;
        field.DataType = model.DataType;
        field.DisplayOrder = model.DisplayOrder;
        field.IsVisible = model.IsVisible;
        field.IsReadOnly = model.IsReadOnly;
        field.IsRequired = model.IsRequired;
        field.IsSensitive = model.IsSensitive;
        field.IncludeInApi = model.IncludeInApi;
        field.LookupType = model.LookupType;
        field.Active = model.Active;
        await db.SaveChangesAsync();

        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index), new { approvalTypeId = field.ApprovalTypeId ?? GenericApprovalType.Id, taskTypeId = field.TaskTypeId ?? 0 });
    }

    // Batched on purpose -- checking boxes on the list doesn't touch the database until
    // this fires, so ticking a few rows and changing your mind costs nothing.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDelete(int[] ids, int approvalTypeId, int taskTypeId)
    {
        if (ids is { Length: > 0 })
        {
            var fields = await db.WorkflowFields.Where(f => ids.Contains(f.Id)).ToListAsync();
            db.WorkflowFields.RemoveRange(fields);
            await db.SaveChangesAsync();
            TempData["Success"] = fields.Count == 1 ? $"Field '{fields[0].FieldLabel}' removed." : $"{fields.Count} fields removed.";
        }
        return RedirectToAction(nameof(Index), new { approvalTypeId, taskTypeId });
    }

    async Task<string> ScopeNameAsync(WorkflowField field)
    {
        if (field.TaskTypeId is not null) return (await db.TaskTypes.FindAsync(field.TaskTypeId))?.Name ?? "(deleted Task Type)";
        return field.ApprovalTypeId is null ? GenericApprovalType.Name : (await db.ApprovalTypes.FindAsync(field.ApprovalTypeId))?.Name ?? "(deleted Approval Type)";
    }
}
