using System.Text.Json;
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
        ViewBag.UsageWarning = await DescribeUsageAsync(id);
        return View(new MailTemplateEditViewModel { Id = t.Id, Name = t.Name, Subject = t.Subject, BodyHtml = t.BodyHtml, IsTableTemplate = t.IsTableTemplate, IsActive = t.IsActive });
    }

    // Used to warn an admin before they deactivate a template that something would actually
    // notice going missing -- PostProcessingRule stores its target as JSON (ActionConfigJson),
    // not a real FK column, so this has to parse each Email-type rule's config rather than
    // query it directly; DigestSchedule.MailTemplateId is a real column.
    async Task<string?> DescribeUsageAsync(int templateId)
    {
        var ruleCount = 0;
        foreach (var r in await db.PostProcessingRules.Where(r => r.ActionType == "Email").ToListAsync())
        {
            try
            {
                using var doc = JsonDocument.Parse(r.ActionConfigJson ?? "{}");
                if (doc.RootElement.TryGetProperty("mailTemplateId", out var t) && t.GetInt32() == templateId) ruleCount++;
            }
            catch { /* malformed config -- not this check's job to flag */ }
        }
        var digestCount = await db.DigestSchedules.CountAsync(s => s.MailTemplateId == templateId);

        var parts = new List<string>();
        if (ruleCount > 0) parts.Add($"{ruleCount} Post-Processing Rule{(ruleCount == 1 ? "" : "s")}");
        if (digestCount > 0) parts.Add($"{digestCount} Digest Schedule{(digestCount == 1 ? "" : "s")}");
        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    // Duplicates a template as a starting point for a variant (e.g. a near-identical
    // subject line for a different Approval Type) -- nothing else references the copy
    // until an admin explicitly points a Post-Processing Rule at it, so there's no risk
    // of it silently going live.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Copy(int id)
    {
        var source = await db.MailTemplates.FindAsync(id);
        if (source is null) return NotFound();

        var baseName = $"{source.Name} (Copy)";
        var name = baseName;
        for (var n = 2; await db.MailTemplates.AnyAsync(t => t.Name == name); n++)
            name = $"{baseName} {n}";

        var copy = new MailTemplate
        {
            Name = name,
            Subject = source.Subject,
            BodyHtml = source.BodyHtml,
            IsTableTemplate = source.IsTableTemplate,
            IsActive = source.IsActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.MailTemplates.Add(copy);
        await db.SaveChangesAsync();

        TempData["Success"] = $"Copied to '{name}'.";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
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
        // Real sends never resolve these against "#" -- PpfExecutor tokens them to one
        // specific approver's one-click link (ApprovalActionLinkService). "#" here is only
        // so Preview shows an actual button instead of the literal, unresolved {{Token}}
        // text an admin would otherwise mistake for a real bug (this is exactly what
        // happened to the Creator-mode Level Pending email before it was rewired off this
        // template: these tokens are CurrentApprover-only, so they only ever resolve there).
        const string previewButtonStyle = "display:inline-block;color:#ffffff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-family:Arial,sans-serif;font-size:14px;";
        var extraTokens = new Dictionary<string, string>
        {
            ["{{LogoUrl}}"] = "/img/jaco-logo-color.png",
            ["{{RequestUrl}}"] = Url.Action("Details", "Requests", new { id = 1 }) ?? "#",
            ["{{ApprovalTimeline}}"] = "<p style=\"color:#6b7280;font-size:13px;\">(the real approval timeline renders here)</p>",
            ["{{ApproveUrl}}"] = "#",
            ["{{RejectUrl}}"] = "#",
            ["{{SendBackUrl}}"] = "#",
            ["{{ApproveButton}}"] = $"<a href=\"#\" style=\"background:#15803d;{previewButtonStyle}\">Approve</a>",
            ["{{RejectButton}}"] = $"<a href=\"#\" style=\"background:#b91c1c;{previewButtonStyle}\">Reject</a>",
            ["{{SendBackButton}}"] = $"<a href=\"#\" style=\"background:#b45309;{previewButtonStyle}\">Send Back</a>",
        };
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
