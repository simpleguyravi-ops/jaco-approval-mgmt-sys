using System.Text;
using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Moves Mail Templates + Post-Processing Rules between environments (e.g. Dev -> QA) as
// one downloadable/uploadable JSON file -- CSV (this app's usual bulk-import convention,
// see RoutingRulesController) is a poor fit here: this data is nested (rule -> criteria,
// rule -> several Approval Types) and carries large HTML bodies. Every reference is
// re-written to a human-readable identifier on export (Approval Type Code, mail template
// Name, AppUser UserName, Task Type Code) and re-resolved against the target database on
// import, since raw Ids are never the same across two separately-seeded databases.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class ConfigSyncController(UnifiedDbContext db) : Controller
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IActionResult> Index()
    {
        var model = new ConfigSyncIndexViewModel
        {
            MailTemplates = await db.MailTemplates.OrderBy(t => t.Name).Select(t => new ConfigSyncIndexItem { Name = t.Name, Detail = t.Subject }).ToListAsync(),
            Rules = await db.PostProcessingRules.OrderBy(r => r.SequenceNo).Select(r => new ConfigSyncIndexItem { Name = r.Name, Detail = r.EventCode + " -> " + r.ActionType }).ToListAsync()
        };
        return View(model);
    }

    // Full export -- the one-click download links on the Mail Templates / Post-Processing
    // Rules screens, and the "everything" case for the selective picker below.
    public Task<IActionResult> Export() => BuildExportFileAsync(null, null);

    // Picking specific items matters most promoting Dev/QA-tested changes into Production:
    // a same-named item already customized there would otherwise get silently overwritten
    // by a full sync -- exporting only what was actually tested avoids that.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ExportSelected(List<string>? templateNames, List<string>? ruleNames) =>
        BuildExportFileAsync(templateNames?.ToHashSet(), ruleNames?.ToHashSet());

    async Task<IActionResult> BuildExportFileAsync(HashSet<string>? templateNameFilter, HashSet<string>? ruleNameFilter)
    {
        var templates = await db.MailTemplates.OrderBy(t => t.Name)
            .Where(t => templateNameFilter == null || templateNameFilter.Contains(t.Name)).ToListAsync();
        var rules = await db.PostProcessingRules.OrderBy(r => r.SequenceNo)
            .Where(r => ruleNameFilter == null || ruleNameFilter.Contains(r.Name)).ToListAsync();
        var typesByRule = (await db.PostProcessingRuleApprovalTypes.ToListAsync())
            .GroupBy(x => x.PostProcessingRuleId).ToDictionary(g => g.Key, g => g.Select(x => x.ApprovalTypeId).ToList());
        var criteriaByRule = (await db.PostProcessingRuleCriteria.ToListAsync())
            .GroupBy(c => c.PostProcessingRuleId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.SortOrder).ToList());
        var typeCodesById = await UniqueDictAsync(db.ApprovalTypes, t => t.Id, t => t.Code);
        var templateNamesById = await UniqueDictAsync(db.MailTemplates, t => t.Id, t => t.Name);
        var userNamesById = await UniqueDictAsync(db.AppUsers, u => u.Id, u => u.UserName);
        var taskTypeCodesById = await UniqueDictAsync(db.TaskTypes, t => t.Id, t => t.Code);

        var export = new ConfigSyncExport
        {
            ExportedAt = DateTime.UtcNow.ToString("o"),
            MailTemplates = templates.Select(t => new ConfigSyncMailTemplate
            {
                Name = t.Name,
                Subject = t.Subject,
                BodyHtml = t.BodyHtml,
                IsTableTemplate = t.IsTableTemplate,
                IsActive = t.IsActive
            }).ToList(),
            PostProcessingRules = rules.Select(r =>
            {
                var item = new ConfigSyncRule
                {
                    Name = r.Name,
                    EventCode = r.EventCode,
                    ActionType = r.ActionType,
                    SequenceNo = r.SequenceNo,
                    Active = r.Active,
                    ApprovalTypeCodes = typesByRule.GetValueOrDefault(r.Id, [])
                        .Select(id => typeCodesById.GetValueOrDefault(id))
                        .Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).ToList(),
                    Criteria = criteriaByRule.GetValueOrDefault(r.Id, [])
                        .Select(c => new ConfigSyncCriteria { FieldKey = c.FieldKey, Operator = c.Operator, ComparisonValue = c.ComparisonValue, LogicalOperator = c.LogicalOperator })
                        .ToList()
                };

                try
                {
                    using var doc = JsonDocument.Parse(r.ActionConfigJson ?? "{}");
                    var root = doc.RootElement;
                    if (r.ActionType == "ApiCall")
                    {
                        item.ApiUrl = root.TryGetProperty("apiUrl", out var au) ? au.GetString() : null;
                        item.ApiAuthHeaderValue = root.TryGetProperty("apiAuthHeaderValue", out var ah) ? ah.GetString() : null;
                    }
                    else if (r.ActionType == "AssignTask")
                    {
                        item.TaskTypeCode = root.TryGetProperty("taskTypeId", out var tt) && tt.ValueKind == JsonValueKind.Number ? taskTypeCodesById.GetValueOrDefault(tt.GetInt32()) : null;
                        item.TaskTitle = root.TryGetProperty("taskTitle", out var tit) ? tit.GetString() : null;
                        item.TaskAssignToMode = root.TryGetProperty("assignToMode", out var am) ? am.GetString() : null;
                        item.TaskAssignToUserName = root.TryGetProperty("assignToUserId", out var au2) && au2.ValueKind == JsonValueKind.Number ? userNamesById.GetValueOrDefault(au2.GetInt32()) : null;
                        item.TaskAssignToDepartment = root.TryGetProperty("assignToDepartment", out var dep) ? dep.GetString() : null;
                        item.TaskDueInDays = root.TryGetProperty("dueInDays", out var did) && did.ValueKind == JsonValueKind.Number ? did.GetInt32() : null;
                        item.TaskContextFieldKeys = root.TryGetProperty("contextFieldKeys", out var cfk) && cfk.ValueKind == JsonValueKind.Array
                            ? cfk.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList() : [];
                    }
                    else
                    {
                        item.MailTemplateName = root.TryGetProperty("mailTemplateId", out var t) && t.ValueKind == JsonValueKind.Number ? templateNamesById.GetValueOrDefault(t.GetInt32()) : null;
                        item.ToMode = root.TryGetProperty("toMode", out var m) ? m.GetString() : null;
                        item.ToAddress = root.TryGetProperty("toAddress", out var a) ? a.GetString() : null;
                        item.ToFieldKey = root.TryGetProperty("toFieldKey", out var fk) ? fk.GetString() : null;
                        item.ToUserName = root.TryGetProperty("toUserId", out var uid) && uid.ValueKind == JsonValueKind.Number ? userNamesById.GetValueOrDefault(uid.GetInt32()) : null;
                        item.CcMode = root.TryGetProperty("ccMode", out var cm) ? cm.GetString() : null;
                        item.CcAddress = root.TryGetProperty("ccAddress", out var ca) ? ca.GetString() : null;
                        item.CcFieldKey = root.TryGetProperty("ccFieldKey", out var ccfk) ? ccfk.GetString() : null;
                        item.IncludeAttachments = root.TryGetProperty("includeAttachments", out var ia) && ia.ValueKind == JsonValueKind.True;
                    }
                }
                catch { /* malformed config exports with blanks -- import will flag it if it can't stand alone */ }

                return item;
            }).ToList()
        };

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(export, JsonOpts));
        return File(bytes, "application/json", $"jaco-unified-ppf-config-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> ImportPreview(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose a JSON file first.";
            return RedirectToAction(nameof(Index));
        }

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
            content = await reader.ReadToEndAsync();

        var preview = await BuildPreviewAsync(content);
        preview.EncodedFile = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        preview.FileName = file.FileName;
        return View("ImportPreview", preview);
    }

    // includeTemplates/includeRules are the checkboxes left checked on the preview screen --
    // default is "everything in the file", but an admin importing into Production can uncheck
    // a same-named item they've deliberately customized there so this import leaves it alone
    // entirely, rather than only choosing what went INTO the file back on the export side.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportConfirm(string encodedFile, List<string>? includeTemplates, List<string>? includeRules)
    {
        string content;
        try { content = Encoding.UTF8.GetString(Convert.FromBase64String(encodedFile)); }
        catch
        {
            TempData["Error"] = "The uploaded file could not be re-read. Please upload it again.";
            return RedirectToAction(nameof(Index));
        }

        // Never trust the hidden round-tripped field blindly -- re-validate exactly as the
        // preview step did before writing anything (same discipline as RoutingRulesController).
        var preview = await BuildPreviewAsync(content);
        if (preview.ErrorCount > 0)
        {
            TempData["Error"] = $"{preview.ErrorCount} item(s) still have errors -- fix the file and re-upload. Nothing was changed.";
            preview.EncodedFile = encodedFile;
            return View("ImportPreview", preview);
        }

        var fullExport = JsonSerializer.Deserialize<ConfigSyncExport>(content, JsonOpts) ?? new ConfigSyncExport();
        var includedTemplateNames = (includeTemplates ?? []).ToHashSet();
        var includedRuleNames = (includeRules ?? []).ToHashSet();
        var export = new ConfigSyncExport
        {
            MailTemplates = fullExport.MailTemplates.Where(t => includedTemplateNames.Contains(t.Name)).ToList(),
            PostProcessingRules = fullExport.PostProcessingRules.Where(r => includedRuleNames.Contains(r.Name)).ToList()
        };
        if (export.MailTemplates.Count == 0 && export.PostProcessingRules.Count == 0)
        {
            TempData["Error"] = "Nothing was selected to import. Nothing was changed.";
            return RedirectToAction(nameof(Index));
        }

        var templatesInserted = 0;
        var templatesUpdated = 0;
        foreach (var t in export.MailTemplates)
        {
            var existing = await db.MailTemplates.FirstOrDefaultAsync(x => x.Name == t.Name);
            if (existing is null)
            {
                existing = new MailTemplate { Name = t.Name, CreatedAt = DateTime.UtcNow };
                db.MailTemplates.Add(existing);
                templatesInserted++;
            }
            else templatesUpdated++;

            existing.Subject = t.Subject;
            existing.BodyHtml = t.BodyHtml;
            existing.IsTableTemplate = t.IsTableTemplate;
            existing.IsActive = t.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Re-fetched after the templates above are saved, so a rule referencing a mail
        // template that's brand new in this same file still resolves to a real Id.
        var typeIdsByCode = await UniqueDictAsync(db.ApprovalTypes, x => x.Code, x => x.Id);
        var templateIdsByName = await UniqueDictAsync(db.MailTemplates, x => x.Name, x => x.Id);
        var userIdsByUserName = await UniqueDictAsync(db.AppUsers, x => x.UserName, x => x.Id);
        var taskTypeIdsByCode = await UniqueDictAsync(db.TaskTypes, x => x.Code, x => x.Id);

        var rulesInserted = 0;
        var rulesUpdated = 0;
        foreach (var r in export.PostProcessingRules)
        {
            var rule = await db.PostProcessingRules.FirstOrDefaultAsync(x => x.Name == r.Name);
            if (rule is null)
            {
                rule = new PostProcessingRule { Name = r.Name };
                db.PostProcessingRules.Add(rule);
                rulesInserted++;
            }
            else rulesUpdated++;

            rule.EventCode = r.EventCode;
            rule.ActionType = r.ActionType;
            rule.SequenceNo = r.SequenceNo;
            rule.Active = r.Active;
            rule.ActionConfigJson = r.ActionType switch
            {
                "ApiCall" => JsonSerializer.Serialize(new { apiUrl = r.ApiUrl, apiAuthHeaderValue = r.ApiAuthHeaderValue }),
                // GetValueOrDefault(int) would silently write 0 (a real, wrong, Id) for a
                // reference that didn't resolve -- e.g. its Mail Template got deselected on
                // this import while the rule that needs it stayed selected. TryGetValue keeps
                // that case an honest null instead.
                "AssignTask" => JsonSerializer.Serialize(new
                {
                    taskTypeId = r.TaskTypeCode is not null && taskTypeIdsByCode.TryGetValue(r.TaskTypeCode, out var ttid) ? ttid : (int?)null,
                    taskTitle = r.TaskTitle,
                    assignToMode = r.TaskAssignToMode,
                    assignToUserId = r.TaskAssignToUserName is not null && userIdsByUserName.TryGetValue(r.TaskAssignToUserName, out var atuid) ? atuid : (int?)null,
                    assignToDepartment = r.TaskAssignToDepartment,
                    dueInDays = r.TaskDueInDays,
                    contextFieldKeys = r.TaskContextFieldKeys
                }),
                _ => JsonSerializer.Serialize(new
                {
                    mailTemplateId = r.MailTemplateName is not null && templateIdsByName.TryGetValue(r.MailTemplateName, out var mtid) ? mtid : (int?)null,
                    toMode = r.ToMode,
                    toAddress = r.ToAddress,
                    toFieldKey = r.ToFieldKey,
                    toUserId = r.ToUserName is not null && userIdsByUserName.TryGetValue(r.ToUserName, out var tuid) ? tuid : (int?)null,
                    ccMode = r.CcMode,
                    ccAddress = r.CcAddress,
                    ccFieldKey = r.CcFieldKey,
                    includeAttachments = r.IncludeAttachments
                })
            };
            await db.SaveChangesAsync();

            db.PostProcessingRuleApprovalTypes.RemoveRange(db.PostProcessingRuleApprovalTypes.Where(x => x.PostProcessingRuleId == rule.Id));
            foreach (var code in r.ApprovalTypeCodes.Distinct())
                if (typeIdsByCode.TryGetValue(code, out var typeId))
                    db.PostProcessingRuleApprovalTypes.Add(new PostProcessingRuleApprovalType { PostProcessingRuleId = rule.Id, ApprovalTypeId = typeId });

            db.PostProcessingRuleCriteria.RemoveRange(db.PostProcessingRuleCriteria.Where(x => x.PostProcessingRuleId == rule.Id));
            var sort = 0;
            foreach (var c in r.Criteria)
                db.PostProcessingRuleCriteria.Add(new PostProcessingRuleCriteria { PostProcessingRuleId = rule.Id, FieldKey = c.FieldKey, Operator = c.Operator, ComparisonValue = c.ComparisonValue, SortOrder = sort++, LogicalOperator = c.LogicalOperator });

            await db.SaveChangesAsync();
        }

        TempData["Success"] = $"Import complete -- {templatesInserted} template(s) added, {templatesUpdated} updated; {rulesInserted} rule(s) added, {rulesUpdated} updated.";
        return RedirectToAction(nameof(Index));
    }

    async Task<ConfigSyncPreview> BuildPreviewAsync(string jsonContent)
    {
        var preview = new ConfigSyncPreview();

        ConfigSyncExport? export;
        try { export = JsonSerializer.Deserialize<ConfigSyncExport>(jsonContent, JsonOpts); }
        catch (Exception ex)
        {
            preview.Templates.Add(new ConfigSyncPreviewItem { Kind = "File", Name = "(parse error)", Errors = { $"Could not parse this as the expected JSON: {ex.Message}" } });
            return preview;
        }
        if (export is null)
        {
            preview.Templates.Add(new ConfigSyncPreviewItem { Kind = "File", Name = "(empty)", Errors = { "File did not contain a recognizable config export." } });
            return preview;
        }

        var existingTemplateNames = (await db.MailTemplates.Select(t => t.Name).ToListAsync()).ToHashSet();
        var existingRuleNames = (await db.PostProcessingRules.Select(r => r.Name).ToListAsync()).ToHashSet();
        var validTypeCodes = (await db.ApprovalTypes.Select(t => t.Code).ToListAsync()).ToHashSet();
        var validUserNames = (await db.AppUsers.Select(u => u.UserName).ToListAsync()).ToHashSet();
        var validTaskTypeCodes = (await db.TaskTypes.Select(t => t.Code).ToListAsync()).ToHashSet();
        var templateNamesInFile = export.MailTemplates.Select(t => t.Name).ToHashSet();

        foreach (var t in export.MailTemplates)
        {
            var item = new ConfigSyncPreviewItem
            {
                Kind = "Mail Template",
                Name = t.Name,
                Action = existingTemplateNames.Contains(t.Name) ? "Update" : "Insert",
                Summary = t.Subject
            };
            if (string.IsNullOrWhiteSpace(t.Name)) item.Errors.Add("Missing Name.");
            if (string.IsNullOrWhiteSpace(t.Subject)) item.Errors.Add("Missing Subject.");
            if (string.IsNullOrWhiteSpace(t.BodyHtml)) item.Errors.Add("Missing Body.");
            preview.Templates.Add(item);
        }

        foreach (var r in export.PostProcessingRules)
        {
            var item = new ConfigSyncPreviewItem
            {
                Kind = "Rule",
                Name = r.Name,
                Action = existingRuleNames.Contains(r.Name) ? "Update" : "Insert",
                Summary = $"{r.EventCode} → {r.ActionType}"
            };
            if (string.IsNullOrWhiteSpace(r.Name)) item.Errors.Add("Missing Name.");
            if (r.ApprovalTypeCodes.Count == 0) item.Errors.Add("No Approval Types listed.");
            foreach (var code in r.ApprovalTypeCodes)
                if (!validTypeCodes.Contains(code)) item.Errors.Add($"Approval Type code '{code}' does not exist on this server.");

            if (r.ActionType == "Email")
            {
                if (string.IsNullOrWhiteSpace(r.MailTemplateName)) item.Errors.Add("Missing mail template reference.");
                else if (!existingTemplateNames.Contains(r.MailTemplateName) && !templateNamesInFile.Contains(r.MailTemplateName))
                    item.Errors.Add($"Mail template '{r.MailTemplateName}' does not exist on this server, and isn't included in this file either.");
                if (r.ToMode == "SpecificUser" && (string.IsNullOrWhiteSpace(r.ToUserName) || !validUserNames.Contains(r.ToUserName)))
                    item.Errors.Add($"Recipient user '{r.ToUserName}' does not exist on this server.");
            }
            else if (r.ActionType == "ApiCall")
            {
                if (string.IsNullOrWhiteSpace(r.ApiUrl)) item.Errors.Add("Missing API URL.");
            }
            else if (r.ActionType == "AssignTask")
            {
                if (string.IsNullOrWhiteSpace(r.TaskTypeCode) || !validTaskTypeCodes.Contains(r.TaskTypeCode))
                    item.Errors.Add($"Task Type code '{r.TaskTypeCode}' does not exist on this server.");
                if (r.TaskAssignToMode == "SpecificUser" && (string.IsNullOrWhiteSpace(r.TaskAssignToUserName) || !validUserNames.Contains(r.TaskAssignToUserName)))
                    item.Errors.Add($"Assignee user '{r.TaskAssignToUserName}' does not exist on this server.");
            }

            preview.Rules.Add(item);
        }

        return preview;
    }

    // A plain ToDictionaryAsync throws if the key selector ever collides (e.g. two users
    // somehow sharing a UserName) -- that's a data-integrity problem this export/import
    // feature shouldn't itself crash on, so the first match silently wins instead.
    static async Task<Dictionary<TKey, TValue>> UniqueDictAsync<TSource, TKey, TValue>(
        IQueryable<TSource> query, Func<TSource, TKey> keySelector, Func<TSource, TValue> valueSelector) where TKey : notnull
    {
        var list = await query.ToListAsync();
        return list.GroupBy(keySelector).ToDictionary(g => g.Key, g => valueSelector(g.First()));
    }
}
