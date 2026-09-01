using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Adding a brand new type of request to the whole platform: name/code here, then its
// field catalog (WorkflowFieldsController) and routing (RoutingRulesController) are
// configured separately -- this screen only owns the type's identity + lifecycle.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class ApprovalTypesController(UnifiedDbContext db) : Controller
{
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
}
