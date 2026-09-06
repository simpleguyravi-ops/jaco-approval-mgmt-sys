using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// "These Approved requests are still missing field X" -- a single fixed recipient (IT,
// Finance, ...) gets one email listing them, each linking straight to that request. See
// DataCompletionReminderService for the actual matching + send logic; this controller only
// owns configuration (which type, which fields, who to notify, on what cadence).
[Authorize(Policy = "UnifiedAdmin")]
public sealed class DataCompletionRemindersController(UnifiedDbContext db, DataCompletionReminderService reminderService) : Controller
{
    public async Task<IActionResult> Index()
    {
        var types = await db.ApprovalTypes.ToDictionaryAsync(t => t.Id, t => t.Name);
        var rules = await db.DataCompletionReminderRules.OrderBy(r => r.ApprovalTypeId).ToListAsync();
        var allFieldsByKey = await db.WorkflowFields.ToListAsync();

        var items = rules.Select(r =>
        {
            List<string> keys;
            try { keys = JsonSerializer.Deserialize<List<string>>(r.FieldKeysJson) ?? []; }
            catch { keys = []; }
            var labels = keys.Select(k => allFieldsByKey.FirstOrDefault(f => f.FieldKey == k && (f.ApprovalTypeId == r.ApprovalTypeId || f.ApprovalTypeId == null))?.FieldLabel ?? k).ToList();

            return new DataCompletionReminderListItem
            {
                Id = r.Id,
                ApprovalTypeName = types.GetValueOrDefault(r.ApprovalTypeId, "(unknown)"),
                FieldLabels = labels,
                RecipientAddress = r.RecipientAddress,
                Enabled = r.Enabled,
                NextRunAtUtc = r.NextRunAtUtc,
                LastRunAtUtc = r.LastRunAtUtc
            };
        }).ToList();

        return View(items);
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? approvalTypeId) => View("Edit", await BuildEditModel(null, approvalTypeId));

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var model = await BuildEditModel(id, null);
        if (model is null) return NotFound();
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, int approvalTypeId, List<string>? fieldKeys, string? recipientAddress, int? mailTemplateId,
        bool enabled, string recurrenceType, int intervalDays, string startTime)
    {
        if (approvalTypeId == 0 || fieldKeys is null || fieldKeys.Count == 0)
        {
            TempData["Error"] = "Approval Type and at least one field are required.";
            return RedirectToAction(id == 0 ? nameof(Create) : nameof(Edit), id == 0 ? new { approvalTypeId } : new { id });
        }
        if (enabled && mailTemplateId is null)
        {
            TempData["Error"] = "Pick a Mail Template before enabling the schedule.";
            return RedirectToAction(id == 0 ? nameof(Create) : nameof(Edit), id == 0 ? new { approvalTypeId } : new { id });
        }
        if (!TimeSpan.TryParse(startTime, out var parsedStartTime)) parsedStartTime = new TimeSpan(9, 0, 0);

        DataCompletionReminderRule rule;
        if (id == 0)
        {
            rule = new DataCompletionReminderRule { ApprovalTypeId = approvalTypeId };
            db.DataCompletionReminderRules.Add(rule);
        }
        else
        {
            rule = await db.DataCompletionReminderRules.SingleAsync(r => r.Id == id);
            rule.ApprovalTypeId = approvalTypeId;
        }

        rule.FieldKeysJson = JsonSerializer.Serialize(fieldKeys);
        rule.RecipientAddress = recipientAddress;
        rule.MailTemplateId = mailTemplateId;
        rule.Enabled = enabled;
        rule.RecurrenceType = recurrenceType == "Weekdays" ? "Weekdays" : "EveryNDays";
        rule.IntervalDays = Math.Max(1, intervalDays);
        rule.StartTime = parsedStartTime;
        rule.UpdatedAt = DateTime.UtcNow;
        rule.UpdatedByUserName = User.Identity?.Name;
        // Recomputed fresh from "now" whenever the rule changes -- simpler and more
        // predictable than trying to preserve the old cadence's anchor across an edit.
        rule.NextRunAtUtc = enabled ? DigestService.ComputeNextRunUtc(rule.RecurrenceType, rule.IntervalDays, rule.StartTime, null) : null;

        await db.SaveChangesAsync();
        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(int id)
    {
        var rule = await db.DataCompletionReminderRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
        if (rule is null) return NotFound();
        if (rule.MailTemplateId is null)
        {
            TempData["Error"] = "Pick and save a Mail Template for this rule before sending manually.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        var run = await reminderService.RunAsync(id, "Manual", User.Identity?.Name);
        TempData[run.Status == "Sent" ? "Success" : "Error"] = run.Status switch
        {
            "Sent" => $"Sent -- {run.MatchingCount} request(s) listed.",
            "Skipped" => $"Not sent: {run.ErrorMessage}",
            _ => $"Failed: {run.ErrorMessage}"
        };
        return RedirectToAction(nameof(Edit), new { id });
    }

    async Task<DataCompletionReminderEditViewModel?> BuildEditModel(int? id, int? approvalTypeIdForCreate)
    {
        var types = await db.ApprovalTypes.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();
        var templates = await db.MailTemplates.Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();

        var model = new DataCompletionReminderEditViewModel
        {
            ApprovalTypes = types.Select(t => (t.Id, t.Name)).ToList(),
            MailTemplates = templates.Select(t => (t.Id, t.Name)).ToList()
        };

        if (id is null)
        {
            model.ApprovalTypeId = approvalTypeIdForCreate ?? 0;
        }
        else
        {
            var rule = await db.DataCompletionReminderRules.SingleOrDefaultAsync(r => r.Id == id);
            if (rule is null) return null;

            model.Id = rule.Id;
            model.ApprovalTypeId = rule.ApprovalTypeId;
            try { model.FieldKeys = JsonSerializer.Deserialize<List<string>>(rule.FieldKeysJson) ?? []; }
            catch { model.FieldKeys = []; }
            model.RecipientAddress = rule.RecipientAddress;
            model.MailTemplateId = rule.MailTemplateId;
            model.Enabled = rule.Enabled;
            model.RecurrenceType = rule.RecurrenceType;
            model.IntervalDays = rule.IntervalDays;
            model.StartTime = rule.StartTime;
            model.NextRunAtUtc = rule.NextRunAtUtc;
            model.LastRunAtUtc = rule.LastRunAtUtc;
            model.RecentRuns = await db.DataCompletionReminderRuns.Where(r => r.RuleId == id).OrderByDescending(r => r.RunAtUtc).Take(10).ToListAsync();
        }

        if (model.ApprovalTypeId != 0)
        {
            var fields = await db.WorkflowFields
                .Where(f => f.Active && (f.ApprovalTypeId == model.ApprovalTypeId || f.ApprovalTypeId == null))
                .OrderBy(f => f.DisplayOrder)
                .Select(f => new { f.FieldKey, f.FieldLabel })
                .ToListAsync();
            model.AvailableFields = fields.Select(f => (f.FieldKey, f.FieldLabel)).ToList();
        }

        return model;
    }
}
