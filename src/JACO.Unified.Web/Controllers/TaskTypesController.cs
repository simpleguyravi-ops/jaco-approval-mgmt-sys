using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
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
}
