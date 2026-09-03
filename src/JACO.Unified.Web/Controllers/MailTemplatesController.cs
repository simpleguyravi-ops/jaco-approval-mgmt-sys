using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

[Authorize(Policy = "UnifiedAdmin")]
public sealed class MailTemplatesController(UnifiedDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? sort, string dir = "asc")
    {
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var desc = dir == "desc";
        IQueryable<MailTemplate> query = db.MailTemplates;
        query = sort switch
        {
            "Subject" => desc ? query.OrderByDescending(t => t.Subject) : query.OrderBy(t => t.Subject),
            "Type" => desc ? query.OrderByDescending(t => t.IsTableTemplate) : query.OrderBy(t => t.IsTableTemplate),
            "Status" => desc ? query.OrderByDescending(t => t.IsActive) : query.OrderBy(t => t.IsActive),
            _ => query.OrderBy(t => t.Name)
        };
        return View(await query.ToListAsync());
    }

    public async Task<IActionResult> Export()
    {
        var templates = await db.MailTemplates.OrderBy(t => t.Name).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(templates,
            ["Name", "Subject", "Type", "Status"],
            t => [t.Name, t.Subject, t.IsTableTemplate ? "Table (digest)" : "Single record", t.IsActive ? "Active" : "Disabled"]);
        return File(bytes, "text/csv", $"mail-templates-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet]
    public IActionResult Create() => View("Edit", new MailTemplateEditViewModel { IsActive = true });

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var t = await db.MailTemplates.FindAsync(id);
        if (t is null) return NotFound();
        return View(new MailTemplateEditViewModel { Id = t.Id, Name = t.Name, Subject = t.Subject, BodyHtml = t.BodyHtml, IsTableTemplate = t.IsTableTemplate, IsActive = t.IsActive });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Preview(MailTemplateEditViewModel model)
    {
        var sample = new MailTemplate { Subject = model.Subject, BodyHtml = model.BodyHtml, IsTableTemplate = model.IsTableTemplate };
        // {{LogoUrl}} is otherwise only ever filled in by PpfExecutor (a real send) as
        // "cid:jaco-logo" -- an inline email attachment reference a browser can't resolve.
        // This preview renders directly in the admin's own browser on this app's own
        // origin, so a normal root-relative path is what actually loads here.
        var extraTokens = new Dictionary<string, string> { ["{{LogoUrl}}"] = "/img/jaco-logo-color.png" };
        var (subject, body) = model.IsTableTemplate
            ? MailMergeService.RenderTable(sample, "Approving Manager", SampleRequests())
            : MailMergeService.RenderSingle(sample, SampleRequests()[0], "Test Creator", extraTokens);

        model.PreviewSubject = subject;
        model.PreviewBody = body;
        return View("Edit", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(MailTemplateEditViewModel model)
    {
        model.Name = (model.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.Subject) || string.IsNullOrWhiteSpace(model.BodyHtml))
        {
            TempData["Error"] = "Name, Subject, and Body are required.";
            return View("Edit", model);
        }

        MailTemplate template;
        if (model.Id == 0)
        {
            if (await db.MailTemplates.AnyAsync(t => t.Name == model.Name))
            {
                TempData["Error"] = "A template with this name already exists.";
                return View("Edit", model);
            }
            template = new MailTemplate { CreatedAt = DateTime.UtcNow };
            db.MailTemplates.Add(template);
        }
        else
        {
            template = await db.MailTemplates.SingleAsync(t => t.Id == model.Id);
        }

        template.Name = model.Name;
        template.Subject = model.Subject;
        template.BodyHtml = model.BodyHtml;
        template.IsTableTemplate = model.IsTableTemplate;
        template.IsActive = model.IsActive;
        template.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index));
    }

    static List<Request> SampleRequests() =>
    [
        new() { RequestNumber = "CR-2026-00001", Subject = "Sample Change Request", Status = "Pending", CurrentLevelNo = 1, CreatedAt = DateTime.UtcNow.AddHours(-3) },
        new() { RequestNumber = "CR-2026-00002", Subject = "Sample Sales Discount", Status = "Pending", CurrentLevelNo = 2, CreatedAt = DateTime.UtcNow.AddHours(-1) },
    ];
}
