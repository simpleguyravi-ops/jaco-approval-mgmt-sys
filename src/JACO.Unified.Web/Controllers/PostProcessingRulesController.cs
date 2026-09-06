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
    // Approve/Send Back/Reject links, not a single combined email.
    public static readonly string[] EventCodes = ["Created", "Resubmit", "Approved", "Completed", "Rejected", "SentBack", "Nudged", "LevelPending"];

    public async Task<IActionResult> Index(string? sort, string dir = "asc")
    {
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var items = await BuildItemsAsync();
        var desc = dir == "desc";
        IOrderedEnumerable<PpfRuleListItem>? ordered = sort switch
        {
            "Name" => desc ? items.OrderByDescending(r => r.Name) : items.OrderBy(r => r.Name),
            "ApprovalType" => desc ? items.OrderByDescending(r => r.ApprovalTypeNames) : items.OrderBy(r => r.ApprovalTypeNames),
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
            ["Name", "Approval Types", "Event", "Action", "Template", "Recipient", "Criteria", "Attachments", "Status"],
            r => [r.Name, r.ApprovalTypeNames, r.EventCode, r.ActionType, r.TemplateName, r.ToMode, r.CriteriaCount == 0 ? "Always" : $"{r.CriteriaCount} condition(s)", r.IncludeAttachments ? "Yes" : "No", r.Active ? "Active" : "Disabled"]);
        return File(bytes, "text/csv", $"post-processing-rules-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    async Task<List<PpfRuleListItem>> BuildItemsAsync()
    {
        var types = await db.ApprovalTypes.ToDictionaryAsync(t => t.Id, t => t.Name);
        var templates = await db.MailTemplates.ToDictionaryAsync(t => t.Id, t => t.Name);
        var users = await db.AppUsers.ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var rules = await db.PostProcessingRules.OrderBy(r => r.SequenceNo).ToListAsync();
        var typesByRule = (await db.PostProcessingRuleApprovalTypes.ToListAsync())
            .GroupBy(x => x.PostProcessingRuleId).ToDictionary(g => g.Key, g => g.Select(x => x.ApprovalTypeId).ToList());
        var criteriaCounts = (await db.PostProcessingRuleCriteria.GroupBy(c => c.PostProcessingRuleId).Select(g => new { RuleId = g.Key, Count = g.Count() }).ToListAsync())
            .ToDictionary(x => x.RuleId, x => x.Count);

        return rules.Select(r =>
        {
            var typeNames = typesByRule.GetValueOrDefault(r.Id, []).Select(id => types.GetValueOrDefault(id, "(unknown)"));

            int? templateId = null;
            var toMode = "Creator";
            var includeAttachments = false;
            var apiUrl = "";
            try
            {
                using var doc = JsonDocument.Parse(r.ActionConfigJson ?? "{}");
                if (r.ActionType == "ApiCall")
                {
                    apiUrl = doc.RootElement.TryGetProperty("apiUrl", out var u) ? u.GetString() ?? "" : "";
                }
                else
                {
                    if (doc.RootElement.TryGetProperty("mailTemplateId", out var t)) templateId = t.GetInt32();
                    if (doc.RootElement.TryGetProperty("toMode", out var m)) toMode = m.GetString() ?? "Creator";
                    includeAttachments = doc.RootElement.TryGetProperty("includeAttachments", out var ia) && ia.ValueKind == JsonValueKind.True;
                    if (toMode == "SpecificUser" && doc.RootElement.TryGetProperty("toUserId", out var uid) && uid.ValueKind == JsonValueKind.Number)
                        toMode = $"User: {users.GetValueOrDefault(uid.GetInt32(), "(deleted user)")}";
                }
            }
            catch { /* malformed config renders as "unknown" below rather than failing the whole list */ }

            return new PpfRuleListItem
            {
                Id = r.Id,
                Name = r.Name,
                ApprovalTypeNames = string.Join(", ", typeNames),
                EventCode = r.EventCode,
                ActionType = r.ActionType,
                TemplateName = r.ActionType == "ApiCall" ? apiUrl : (templateId.HasValue ? templates.GetValueOrDefault(templateId.Value, "(deleted template)") : "(none)"),
                ToMode = r.ActionType == "ApiCall" ? "-" : toMode,
                IncludeAttachments = includeAttachments,
                CriteriaCount = criteriaCounts.GetValueOrDefault(r.Id, 0),
                Active = r.Active
            };
        }).ToList();
    }

    [HttpGet]
    public async Task<IActionResult> Create(List<int>? approvalTypeId) => View("Edit", await BuildEditModel(null, approvalTypeId));

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var model = await BuildEditModel(id, null);
        if (model is null) return NotFound();
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PpfRuleEditViewModel model, List<int>? approvalTypeIds, List<string>? criteriaFieldKey, List<string>? criteriaOperator, List<string>? criteriaValue, List<string>? criteriaLogicalOperator)
    {
        model.ApprovalTypeIds = approvalTypeIds ?? [];
        model.Name = (model.Name ?? "").Trim();

        // Whatever criteria rows were just typed in, not what's saved -- so a validation
        // error below doesn't wipe an admin's in-progress edits to the criteria builder.
        List<PpfCriteriaRow> PostedCriteria()
        {
            var rows = new List<PpfCriteriaRow>();
            for (var i = 0; i < (criteriaFieldKey?.Count ?? 0); i++)
                rows.Add(new PpfCriteriaRow
                {
                    FieldKey = criteriaFieldKey![i] ?? "",
                    Operator = i < criteriaOperator?.Count ? criteriaOperator[i] : "=",
                    ComparisonValue = i < criteriaValue?.Count ? criteriaValue[i] ?? "" : "",
                    LogicalOperator = i < criteriaLogicalOperator?.Count && string.Equals(criteriaLogicalOperator[i], "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND"
                });
            while (rows.Count < 3) rows.Add(new PpfCriteriaRow());
            return rows;
        }

        async Task<IActionResult> Reject(string message)
        {
            TempData["Error"] = message;
            var refreshed = await BuildEditModel(model.Id == 0 ? null : model.Id, model.ApprovalTypeIds) ?? model;
            refreshed.Id = model.Id; refreshed.Name = model.Name; refreshed.ApprovalTypeIds = model.ApprovalTypeIds; refreshed.EventCode = model.EventCode;
            refreshed.ActionType = model.ActionType; refreshed.MailTemplateId = model.MailTemplateId; refreshed.ToMode = model.ToMode; refreshed.ToAddress = model.ToAddress;
            refreshed.ToFieldKey = model.ToFieldKey; refreshed.ToUserId = model.ToUserId; refreshed.SequenceNo = model.SequenceNo; refreshed.Active = model.Active;
            refreshed.CcMode = model.CcMode; refreshed.CcAddress = model.CcAddress; refreshed.CcFieldKey = model.CcFieldKey;
            refreshed.IncludeAttachments = model.IncludeAttachments; refreshed.ApiUrl = model.ApiUrl; refreshed.ApiAuthHeaderValue = model.ApiAuthHeaderValue;
            refreshed.Criteria = PostedCriteria();
            return View("Edit", refreshed);
        }

        if (string.IsNullOrEmpty(model.Name)) return await Reject("Give this rule a name.");
        if (model.ApprovalTypeIds.Count == 0) return await Reject("Pick at least one Approval Type.");
        if (model.ActionType == "Email" && model.MailTemplateId == 0) return await Reject("Mail Template is required for a Send Email rule.");
        if (model.ActionType == "Email" && model.ToMode == "SpecificUser" && model.ToUserId is null) return await Reject("Pick which user should receive this email.");
        if (model.ActionType == "ApiCall" && string.IsNullOrWhiteSpace(model.ApiUrl)) return await Reject("API URL is required for a Call an API rule.");

        var config = model.ActionType == "ApiCall"
            ? JsonSerializer.Serialize(new { apiUrl = model.ApiUrl, apiAuthHeaderValue = model.ApiAuthHeaderValue })
            : JsonSerializer.Serialize(new
            {
                mailTemplateId = model.MailTemplateId, toMode = model.ToMode, toAddress = model.ToAddress, toFieldKey = model.ToFieldKey, toUserId = model.ToUserId,
                ccMode = model.CcMode, ccAddress = model.CcAddress, ccFieldKey = model.CcFieldKey,
                includeAttachments = model.IncludeAttachments
            });

        PostProcessingRule rule;
        if (model.Id == 0)
        {
            rule = new PostProcessingRule { ActionType = model.ActionType };
            db.PostProcessingRules.Add(rule);
        }
        else
        {
            rule = await db.PostProcessingRules.SingleAsync(r => r.Id == model.Id);
            rule.ActionType = model.ActionType;
        }

        rule.Name = model.Name;
        rule.EventCode = model.EventCode;
        rule.ActionConfigJson = config;
        rule.SequenceNo = model.SequenceNo;
        rule.Active = model.Active;

        await db.SaveChangesAsync();

        db.PostProcessingRuleApprovalTypes.RemoveRange(db.PostProcessingRuleApprovalTypes.Where(x => x.PostProcessingRuleId == rule.Id));
        foreach (var typeId in model.ApprovalTypeIds.Distinct())
            db.PostProcessingRuleApprovalTypes.Add(new PostProcessingRuleApprovalType { PostProcessingRuleId = rule.Id, ApprovalTypeId = typeId });

        await SaveCriteriaAsync(rule.Id, criteriaFieldKey, criteriaOperator, criteriaValue, criteriaLogicalOperator);

        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index));
    }

    // Replace-all, same as RoutingRulesController's own criteria save -- simpler and safer
    // than trying to diff/patch existing rows, and this only ever runs from an admin
    // clicking Save, not on any hot path. A blank Field Key skips that row entirely (the
    // Edit view always renders a fixed number of rows so an admin doesn't need explicit
    // add/remove buttons), and SortOrder is assigned by position among the SURVIVING rows,
    // not the original slot index, so a gap left by a skipped row never breaks grouping.
    async Task SaveCriteriaAsync(int ruleId, List<string>? fieldKeys, List<string>? operators, List<string>? values, List<string>? logicalOperators)
    {
        db.PostProcessingRuleCriteria.RemoveRange(db.PostProcessingRuleCriteria.Where(c => c.PostProcessingRuleId == ruleId));

        if (fieldKeys is not null)
        {
            var sortOrder = 0;
            for (var i = 0; i < fieldKeys.Count; i++)
            {
                var fieldKey = fieldKeys[i]?.Trim();
                if (string.IsNullOrEmpty(fieldKey)) continue;

                db.PostProcessingRuleCriteria.Add(new PostProcessingRuleCriteria
                {
                    PostProcessingRuleId = ruleId,
                    FieldKey = fieldKey,
                    Operator = i < operators?.Count ? operators[i] : "=",
                    ComparisonValue = i < values?.Count ? values[i]?.Trim() ?? "" : "",
                    SortOrder = sortOrder++,
                    LogicalOperator = i < logicalOperators?.Count && string.Equals(logicalOperators[i], "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND"
                });
            }
        }

        await db.SaveChangesAsync();
    }

    async Task<PpfRuleEditViewModel?> BuildEditModel(int? id, List<int>? approvalTypeIdsForCreate)
    {
        var types = await db.ApprovalTypes.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();
        var templates = await db.MailTemplates.Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();
        var users = await db.AppUsers.Where(u => u.IsActive && u.Email != null && u.Email != "").OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName }).ToListAsync();

        var model = new PpfRuleEditViewModel
        {
            ApprovalTypes = types.Select(t => (t.Id, t.Name)).ToList(),
            MailTemplates = templates.Select(t => (t.Id, t.Name)).ToList(),
            Users = users.Select(u => (u.Id, u.DisplayName)).ToList()
        };

        if (id is null)
        {
            model.ApprovalTypeIds = approvalTypeIdsForCreate ?? [];
        }
        else
        {
            var rule = await db.PostProcessingRules.SingleOrDefaultAsync(r => r.Id == id);
            if (rule is null) return null;

            model.Id = rule.Id;
            model.Name = rule.Name;
            model.EventCode = rule.EventCode;
            model.ActionType = rule.ActionType;
            model.SequenceNo = rule.SequenceNo;
            model.Active = rule.Active;
            model.ApprovalTypeIds = await db.PostProcessingRuleApprovalTypes.Where(x => x.PostProcessingRuleId == id).Select(x => x.ApprovalTypeId).ToListAsync();

            try
            {
                using var doc = JsonDocument.Parse(rule.ActionConfigJson ?? "{}");
                if (rule.ActionType == "ApiCall")
                {
                    if (doc.RootElement.TryGetProperty("apiUrl", out var au)) model.ApiUrl = au.GetString();
                    if (doc.RootElement.TryGetProperty("apiAuthHeaderValue", out var ah)) model.ApiAuthHeaderValue = ah.GetString();
                }
                else
                {
                    if (doc.RootElement.TryGetProperty("mailTemplateId", out var t)) model.MailTemplateId = t.GetInt32();
                    if (doc.RootElement.TryGetProperty("toMode", out var m)) model.ToMode = m.GetString() ?? "Creator";
                    if (doc.RootElement.TryGetProperty("toAddress", out var a)) model.ToAddress = a.GetString();
                    if (doc.RootElement.TryGetProperty("toFieldKey", out var fk)) model.ToFieldKey = fk.GetString();
                    if (doc.RootElement.TryGetProperty("toUserId", out var uid) && uid.ValueKind == JsonValueKind.Number) model.ToUserId = uid.GetInt32();
                    if (doc.RootElement.TryGetProperty("ccMode", out var cm)) model.CcMode = cm.GetString() ?? "None";
                    if (doc.RootElement.TryGetProperty("ccAddress", out var ca)) model.CcAddress = ca.GetString();
                    if (doc.RootElement.TryGetProperty("ccFieldKey", out var cfk)) model.CcFieldKey = cfk.GetString();
                    model.IncludeAttachments = doc.RootElement.TryGetProperty("includeAttachments", out var ia) && ia.ValueKind == JsonValueKind.True;
                }
            }
            catch { /* leave defaults */ }

            model.Criteria = await db.PostProcessingRuleCriteria.Where(c => c.PostProcessingRuleId == id).OrderBy(c => c.SortOrder)
                .Select(c => new PpfCriteriaRow { FieldKey = c.FieldKey, Operator = c.Operator, ComparisonValue = c.ComparisonValue, LogicalOperator = c.LogicalOperator })
                .ToListAsync();
        }

        if (model.ApprovalTypeIds.Count > 0)
        {
            var fields = await db.WorkflowFields
                .Where(f => f.Active && (f.ApprovalTypeId == null || model.ApprovalTypeIds.Contains(f.ApprovalTypeId.Value)))
                .OrderBy(f => f.DisplayOrder)
                .Select(f => new { f.FieldKey, f.FieldLabel })
                .ToListAsync();
            // Union across every selected type -- de-duplicated by FieldKey (a field with
            // the same key shouldn't need to appear twice just because two selected types
            // both offer it).
            model.AvailableFields = fields.GroupBy(f => f.FieldKey).Select(g => (g.Key, g.First().FieldLabel)).ToList();
        }

        // The criteria builder always renders a fixed number of rows (blank Field Key =
        // skip, see SaveCriteriaAsync) -- pad up to that count so there's always at least
        // one empty row to fill in, whether this is a brand new rule or one that already
        // has criteria saved.
        while (model.Criteria.Count < 3) model.Criteria.Add(new PpfCriteriaRow());

        return model;
    }
}
