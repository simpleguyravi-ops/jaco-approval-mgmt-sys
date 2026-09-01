using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// One shared picklist table for every Dropdown WorkflowField across every Approval Type --
// a field just names the LookupType it draws from (see WorkflowField.LookupType).
[Authorize(Policy = "UnifiedAdmin")]
public sealed class PicklistValuesController(UnifiedDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? lookupType)
    {
        ViewBag.LookupTypes = await db.PicklistValues.Select(p => p.LookupType).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.Selected = lookupType;
        var values = string.IsNullOrEmpty(lookupType)
            ? []
            : await db.PicklistValues.Where(p => p.LookupType == lookupType).OrderBy(p => p.SortOrder).ThenBy(p => p.DisplayText).ToListAsync();
        return View(values);
    }

    [HttpGet]
    public IActionResult Create(string? lookupType) => View(new PicklistValue { LookupType = lookupType ?? "", Active = true, SortOrder = 10 });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PicklistValue model)
    {
        if (string.IsNullOrWhiteSpace(model.LookupType) || string.IsNullOrWhiteSpace(model.Value))
        {
            TempData["Error"] = "Lookup Type and Value are required.";
            return View(model);
        }

        db.PicklistValues.Add(model);
        await db.SaveChangesAsync();
        TempData["Success"] = "Added.";
        return RedirectToAction(nameof(Index), new { lookupType = model.LookupType });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var value = await db.PicklistValues.FindAsync(id);
        return value is null ? NotFound() : View(value);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, PicklistValue model)
    {
        var value = await db.PicklistValues.FindAsync(id);
        if (value is null) return NotFound();

        value.DisplayText = model.DisplayText;
        value.SortOrder = model.SortOrder;
        value.Active = model.Active;
        value.ExtraData = model.ExtraData;
        await db.SaveChangesAsync();

        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index), new { lookupType = value.LookupType });
    }
}
