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
public sealed class PostProcessingRulesController(UnifiedDbContext db) : Controller
{
    // "Completed" fires alongside "Approved" (same condition: no levels remain) so a PPF
    // rule can be written against whichever label is unambiguous for its purpose.
    // "LevelPending" fires whenever the CURRENT level's approver(s) change -- initial submit
    // (level 1), resubmit after Sent Back, and advancing to the next level on Approve. Pair
    // it with ToMode=CurrentApprover to notify whoever needs to act right now; unlike the
    // other events it sends one personalized email per approver with working one-click
    // Approve/Reject links, not a single combined email.
    public static readonly string[] EventCodes = ["Created", "Resubmit", "Approved", "Completed", "Rejected", "SentBack", "Nudged", "LevelPending"];

    public async Task<IActionResult> Index(string? sort, string dir = "asc")
    {
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var items = await BuildItemsAsync();
        var desc = dir == "desc";
        IOrderedEnumerable<PpfRuleListItem>? ordered = sort switch
        {
            "ApprovalType" => desc ? items.OrderByDescending(r => r.ApprovalTypeName) : items.OrderBy(r => r.ApprovalTypeName),
            "Event" => desc ? items.OrderByDescending(r => r.EventCode) : items.OrderBy(r => r.EventCode),
            "Template" => desc ? items.OrderByDescending(r => r.TemplateName) : items.OrderBy(r => r.TemplateName),
            "Recipient" => desc ? items.OrderByDescending(r => r.ToMode) : items.OrderBy(r => r.ToMode),
            "Status" => desc ? items.OrderByDescending(r => r.Active) : items.OrderBy(r => r.Active),
            _ => null
        };
        if (ordered is not null) items = ordered.ToList();
        return View(items);
    }

    public async Task<IActionResult> Export()
    {
        var items = await BuildItemsAsync();
        var bytes = CsvHelper.ToCsvBytes(items,
            ["Approval Type", "Event", "Template", "Recipient", "Status"],
            r => [r.ApprovalTypeName, r.EventCode, r.TemplateName, r.ToMode, r.Active ? "Active" : "Disabled"]);
        return File(bytes, "text/csv", $"post-processing-rules-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    async Task<List<PpfRuleListItem>> BuildItemsAsync()
    {
        var types = await db.ApprovalTypes.ToDictionaryAsync(t => t.Id, t => t.Name);
        var templates = await db.MailTemplates.ToDictionaryAsync(t => t.Id, t => t.Name);
        var rules = await db.PostProcessingRules.Where(r => r.ActionType == "Email").OrderBy(r => r.ApprovalTypeId).ThenBy(r => r.SequenceNo).ToListAsync();

        return rules.Select(r =>
        {
            int? templateId = null;
            var toMode = "Creator";
            try
            {
                using var doc = JsonDocument.Parse(r.ActionConfigJson ?? "{}");
                if (doc.RootElement.TryGetProperty("mailTemplateId", out var t)) templateId = t.GetInt32();
                if (doc.RootElement.TryGetProperty("toMode", out var m)) toMode = m.GetString() ?? "Creator";
            }
            catch { /* malformed config renders as "unknown" below rather than failing the whole list */ }

            return new PpfRuleListItem
            {
                Id = r.Id,
                ApprovalTypeName = types.GetValueOrDefault(r.ApprovalTypeId, "(unknown)"),
                EventCode = r.EventCode,
                TemplateName = templateId.HasValue ? templates.GetValueOrDefault(templateId.Value, "(deleted template)") : "(none)",
                ToMode = toMode,
                Active = r.Active
            };
        }).ToList();
    }

    [HttpGet]
    public async Task<IActionResult> Create() => View("Edit", await BuildEditModel(null));

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var model = await BuildEditModel(id);
        if (model is null) return NotFound();
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PpfRuleEditViewModel model)
    {
        if (model.ApprovalTypeId == 0 || model.MailTemplateId == 0)
        {
            TempData["Error"] = "Approval Type and Mail Template are required.";
            var refreshed = await BuildEditModel(model.Id == 0 ? null : model.Id) ?? model;
            refreshed.Id = model.Id; refreshed.ApprovalTypeId = model.ApprovalTypeId; refreshed.EventCode = model.EventCode;
            refreshed.MailTemplateId = model.MailTemplateId; refreshed.ToMode = model.ToMode; refreshed.ToAddress = model.ToAddress;
            refreshed.ToFieldKey = model.ToFieldKey; refreshed.SequenceNo = model.SequenceNo; refreshed.Active = model.Active;
            return View("Edit", refreshed);
        }

        var config = JsonSerializer.Serialize(new { mailTemplateId = model.MailTemplateId, toMode = model.ToMode, toAddress = model.ToAddress, toFieldKey = model.ToFieldKey });

        PostProcessingRule rule;
        if (model.Id == 0)
        {
            rule = new PostProcessingRule { ActionType = "Email" };
            db.PostProcessingRules.Add(rule);
        }
        else
        {
            rule = await db.PostProcessingRules.SingleAsync(r => r.Id == model.Id);
        }

        rule.ApprovalTypeId = model.ApprovalTypeId;
        rule.EventCode = model.EventCode;
        rule.ActionConfigJson = config;
        rule.SequenceNo = model.SequenceNo;
        rule.Active = model.Active;

        await db.SaveChangesAsync();
        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index));
    }

    async Task<PpfRuleEditViewModel?> BuildEditModel(int? id)
    {
        var types = await db.ApprovalTypes.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();
        var templates = await db.MailTemplates.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();

        var model = new PpfRuleEditViewModel
        {
            ApprovalTypes = types.Select(t => (t.Id, t.Name)).ToList(),
            MailTemplates = templates.Select(t => (t.Id, t.Name)).ToList()
        };
        if (id is null) return model;

        var rule = await db.PostProcessingRules.SingleOrDefaultAsync(r => r.Id == id);
        if (rule is null) return null;

        model.Id = rule.Id;
        model.ApprovalTypeId = rule.ApprovalTypeId;
        model.EventCode = rule.EventCode;
        model.SequenceNo = rule.SequenceNo;
        model.Active = rule.Active;

        try
        {
            using var doc = JsonDocument.Parse(rule.ActionConfigJson ?? "{}");
            if (doc.RootElement.TryGetProperty("mailTemplateId", out var t)) model.MailTemplateId = t.GetInt32();
            if (doc.RootElement.TryGetProperty("toMode", out var m)) model.ToMode = m.GetString() ?? "Creator";
            if (doc.RootElement.TryGetProperty("toAddress", out var a)) model.ToAddress = a.GetString();
            if (doc.RootElement.TryGetProperty("toFieldKey", out var fk)) model.ToFieldKey = fk.GetString();
        }
        catch { /* leave defaults */ }

        return model;
    }
}
