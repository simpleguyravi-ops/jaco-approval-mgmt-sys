using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Rule Builder: which submitted-field criteria route a request to which approver chain.
// Criteria field keys are drawn from the same WorkflowFields catalog the Create/Edit form
// itself renders from -- one config screen feeds both.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class RoutingRulesController(UnifiedDbContext db) : Controller
{
    public static readonly string[] Operators = ["=", "!=", "CONTAINS", "STARTSWITH", "ENDSWITH", "IN", ">", ">=", "<", "<="];
    public static readonly string[] Modes = ["ANY_ONE", "ALL", "MAJORITY", "MINIMUM_COUNT"];

    public async Task<IActionResult> Index(int approvalTypeId, string? sort, string dir = "asc")
    {
        var types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
        if (approvalTypeId == 0 && types.Count > 0) approvalTypeId = types[0].Id;
        ViewBag.ApprovalType = types.FirstOrDefault(t => t.Id == approvalTypeId);
        ViewBag.Types = types;
        ViewBag.SelectedTypeId = approvalTypeId;
        ViewBag.Sort = sort; ViewBag.Dir = dir;

        var items = await BuildItemsAsync(approvalTypeId);
        var desc = dir == "desc";
        IOrderedEnumerable<RoutingRuleListItem>? ordered = sort switch
        {
            "Rule" => desc ? items.OrderByDescending(r => r.RuleName) : items.OrderBy(r => r.RuleName),
            "Criteria" => desc ? items.OrderByDescending(r => r.CriteriaSummary) : items.OrderBy(r => r.CriteriaSummary),
            "Levels" => desc ? items.OrderByDescending(r => r.LevelCount) : items.OrderBy(r => r.LevelCount),
            "Status" => desc ? items.OrderByDescending(r => r.Active) : items.OrderBy(r => r.Active),
            "Priority" => desc ? items.OrderByDescending(r => r.Priority) : items.OrderBy(r => r.Priority),
            _ => null
        };
        if (ordered is not null) items = ordered.ToList();

        return View(items);
    }

    public async Task<IActionResult> Export(int approvalTypeId)
    {
        var items = await BuildItemsAsync(approvalTypeId);
        var bytes = CsvHelper.ToCsvBytes(items,
            ["Priority", "Rule", "Criteria", "Levels", "Status"],
            r => [r.Priority.ToString(), r.RuleName, r.CriteriaSummary, r.LevelCount.ToString(), r.Active ? "Active" : "Disabled"]);
        return File(bytes, "text/csv", $"routing-rules-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    async Task<List<RoutingRuleListItem>> BuildItemsAsync(int approvalTypeId)
    {
        var version = await db.WorkflowVersions.SingleOrDefaultAsync(v => v.ApprovalTypeId == approvalTypeId && v.IsCurrent);
        if (version is null) return [];

        var rules = await db.RoutingRules.Where(r => r.WorkflowVersionId == version.Id).OrderBy(r => r.Priority).ToListAsync();
        var items = new List<RoutingRuleListItem>();
        foreach (var rule in rules)
        {
            var criteria = await db.RoutingRuleCriteria.Where(c => c.RoutingRuleId == rule.Id).OrderBy(c => c.SortOrder).ToListAsync();
            var levelCount = await db.WorkflowSteps.CountAsync(s => s.RoutingRuleId == rule.Id);
            items.Add(new RoutingRuleListItem
            {
                Id = rule.Id,
                RuleName = rule.RuleName,
                Priority = rule.Priority,
                Active = rule.Active,
                LevelCount = levelCount,
                CriteriaSummary = criteria.Count == 0 ? "(matches any)" : string.Join(" AND ", criteria.Select(c => $"{c.FieldKey} {c.Operator} {c.ComparisonValue}"))
            });
        }
        return items;
    }

    [HttpGet]
    public async Task<IActionResult> Create(int approvalTypeId) => View("Edit", await BuildFormModelAsync(approvalTypeId, null));

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var rule = await db.RoutingRules.FindAsync(id);
        if (rule is null) return NotFound();
        var version = await db.WorkflowVersions.FindAsync(rule.WorkflowVersionId);
        return View(await BuildFormModelAsync(version!.ApprovalTypeId, id));
    }

    async Task<RuleFormViewModel> BuildFormModelAsync(int approvalTypeId, int? ruleId)
    {
        var criteriaRows = Enumerable.Range(0, RuleFormViewModel.MaxCriteriaRows).Select(_ => new CriteriaFormRow()).ToList();
        var levelRows = Enumerable.Range(1, RuleFormViewModel.MaxLevels).Select(n => new LevelFormRow { LevelNo = n }).ToList();
        RoutingRule? rule = null;

        if (ruleId is not null)
        {
            rule = await db.RoutingRules.FindAsync(ruleId.Value);
            var existingCriteria = await db.RoutingRuleCriteria.Where(c => c.RoutingRuleId == ruleId).OrderBy(c => c.SortOrder).ToListAsync();
            for (var i = 0; i < existingCriteria.Count && i < criteriaRows.Count; i++)
                criteriaRows[i] = new CriteriaFormRow { FieldKey = existingCriteria[i].FieldKey, Operator = existingCriteria[i].Operator, ComparisonValue = existingCriteria[i].ComparisonValue };

            var steps = await db.WorkflowSteps.Where(s => s.RoutingRuleId == ruleId).OrderBy(s => s.LevelNo).ToListAsync();
            for (var n = 0; n < levelRows.Count; n++)
            {
                var step = steps.SingleOrDefault(s => s.LevelNo == n + 1);
                if (step is null) continue;
                var approverIds = await db.WorkflowStepApprovers.Where(a => a.WorkflowStepId == step.Id).Select(a => a.UserId).ToListAsync();
                levelRows[n] = new LevelFormRow { LevelNo = n + 1, Mode = step.Mode, RequiredCount = step.RequiredCount, ApproverUserIds = approverIds };
            }
        }
        else
        {
            // A running suggestion instead of a fixed 10, so a second/third rule for the
            // same type doesn't start as a Priority collision waiting to happen.
            var version = await db.WorkflowVersions.SingleOrDefaultAsync(v => v.ApprovalTypeId == approvalTypeId && v.IsCurrent);
            var maxPriority = version is null ? (int?)null : await db.RoutingRules.Where(r => r.WorkflowVersionId == version.Id && r.Active).MaxAsync(r => (int?)r.Priority);
            rule = new RoutingRule { Priority = (maxPriority ?? 0) + 10, Active = true };
        }

        return new RuleFormViewModel
        {
            ApprovalTypeId = approvalTypeId,
            Rule = ruleId is null ? null : rule,
            Criteria = criteriaRows,
            Levels = levelRows,
            AvailableFields = await db.WorkflowFields.Where(f => f.ApprovalTypeId == approvalTypeId || f.ApprovalTypeId == null).OrderBy(f => f.DisplayOrder).ToListAsync(),
            Users = await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync(),
            DefaultPriority = rule?.Priority ?? 10
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int approvalTypeId, string ruleName, int priority, bool active,
        List<string> criteriaFieldKey, List<string> criteriaOperator, List<string> criteriaValue,
        List<string> levelMode, List<int?> levelRequiredCount, [FromForm] Dictionary<string, List<int>> levelApprovers)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            TempData["Error"] = "A rule name is required.";
            return RedirectToAction(nameof(Create), new { approvalTypeId });
        }

        var version = await GetOrCreateCurrentVersionAsync(approvalTypeId);
        var rule = new RoutingRule { WorkflowVersionId = version.Id, RuleName = ruleName, Priority = priority, Active = active };
        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync();

        await SaveCriteriaAndLevelsAsync(rule.Id, version.Id, criteriaFieldKey, criteriaOperator, criteriaValue, levelMode, levelRequiredCount, levelApprovers);

        TempData["Success"] = $"Rule '{ruleName}' created.";
        return RedirectToAction(nameof(Index), new { approvalTypeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, string ruleName, int priority, bool active,
        List<string> criteriaFieldKey, List<string> criteriaOperator, List<string> criteriaValue,
        List<string> levelMode, List<int?> levelRequiredCount, [FromForm] Dictionary<string, List<int>> levelApprovers)
    {
        var rule = await db.RoutingRules.FindAsync(id);
        if (rule is null) return NotFound();
        var version = await db.WorkflowVersions.FindAsync(rule.WorkflowVersionId);

        if (string.IsNullOrWhiteSpace(ruleName))
        {
            TempData["Error"] = "A rule name is required.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        rule.RuleName = ruleName;
        rule.Priority = priority;
        rule.Active = active;

        db.RoutingRuleCriteria.RemoveRange(db.RoutingRuleCriteria.Where(c => c.RoutingRuleId == id));
        var oldSteps = await db.WorkflowSteps.Where(s => s.RoutingRuleId == id).ToListAsync();
        db.WorkflowStepApprovers.RemoveRange(db.WorkflowStepApprovers.Where(a => oldSteps.Select(s => s.Id).Contains(a.WorkflowStepId)));
        db.WorkflowSteps.RemoveRange(oldSteps);
        await db.SaveChangesAsync();

        await SaveCriteriaAndLevelsAsync(id, rule.WorkflowVersionId, criteriaFieldKey, criteriaOperator, criteriaValue, levelMode, levelRequiredCount, levelApprovers);

        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Index), new { approvalTypeId = version!.ApprovalTypeId });
    }

    async Task SaveCriteriaAndLevelsAsync(int ruleId, int workflowVersionId, List<string> fieldKeys, List<string> operators, List<string> values, List<string> modes, List<int?> requiredCounts, Dictionary<string, List<int>>? levelApprovers)
    {
        for (var i = 0; i < fieldKeys.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(fieldKeys[i]) || string.IsNullOrWhiteSpace(values[i])) continue;
            db.RoutingRuleCriteria.Add(new RoutingRuleCriteria { RoutingRuleId = ruleId, FieldKey = fieldKeys[i].Trim(), Operator = operators[i], ComparisonValue = values[i], SortOrder = i });
        }

        for (var levelNo = 1; levelNo <= modes.Count; levelNo++)
        {
            var approverIds = levelApprovers?.GetValueOrDefault(levelNo.ToString()) ?? [];
            if (approverIds.Count == 0) continue;

            var mode = modes[levelNo - 1];
            var step = new WorkflowStep
            {
                WorkflowVersionId = workflowVersionId,
                RoutingRuleId = ruleId,
                LevelNo = levelNo,
                Mode = mode,
                RequiredCount = mode == "MINIMUM_COUNT" ? requiredCounts.ElementAtOrDefault(levelNo - 1) : null
            };
            db.WorkflowSteps.Add(step);
            await db.SaveChangesAsync();

            foreach (var userId in approverIds)
                db.WorkflowStepApprovers.Add(new WorkflowStepApprover { WorkflowStepId = step.Id, UserId = userId });
        }
        await db.SaveChangesAsync();
    }

    // ---------- Matrix Rule (UI-only preview) ----------
    // Three-step flow: configure axes -> fill in the resulting grid -> preview exactly what
    // real rules would be created. No database write anywhere in this section yet -- see the
    // comment on MatrixConfigViewModel.

    [HttpGet]
    public async Task<IActionResult> Matrix(int approvalTypeId)
    {
        var model = new MatrixConfigViewModel
        {
            ApprovalTypeId = approvalTypeId,
            Priority = await SuggestPriorityAsync(approvalTypeId),
            Active = true,
            Criteria = Enumerable.Range(0, 4).Select(_ => new CriteriaFormRow()).ToList(),
            AvailableFields = await db.WorkflowFields.Where(f => f.ApprovalTypeId == approvalTypeId || f.ApprovalTypeId == null).OrderBy(f => f.DisplayOrder).ToListAsync()
        };
        ViewBag.ApprovalTypeName = (await db.ApprovalTypes.FindAsync(approvalTypeId))?.Name ?? "";
        return View(model);
    }

    async Task<int> SuggestPriorityAsync(int approvalTypeId)
    {
        var version = await db.WorkflowVersions.SingleOrDefaultAsync(v => v.ApprovalTypeId == approvalTypeId && v.IsCurrent);
        var maxPriority = version is null ? (int?)null : await db.RoutingRules.Where(r => r.WorkflowVersionId == version.Id && r.Active).MaxAsync(r => (int?)r.Priority);
        return (maxPriority ?? 0) + 10;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MatrixGrid(int approvalTypeId, string matrixName, string columnFieldKey, string columnValues,
        string? rowFieldKey, string? rowBands, int priority, bool active,
        List<string>? criteriaFieldKey, List<string>? criteriaOperator, List<string>? criteriaValue)
    {
        criteriaFieldKey ??= []; criteriaOperator ??= []; criteriaValue ??= [];

        if (string.IsNullOrWhiteSpace(matrixName) || string.IsNullOrWhiteSpace(columnFieldKey) || string.IsNullOrWhiteSpace(columnValues))
        {
            TempData["Error"] = "Matrix Name, Column Field, and Column Values are required.";
            return RedirectToAction(nameof(Matrix), new { approvalTypeId });
        }

        var columns = columnValues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (columns.Count == 0)
        {
            TempData["Error"] = "Enter at least one column value.";
            return RedirectToAction(nameof(Matrix), new { approvalTypeId });
        }

        var rowLabels = new List<string>();
        if (!string.IsNullOrWhiteSpace(rowFieldKey) && !string.IsNullOrWhiteSpace(rowBands))
        {
            foreach (var band in rowBands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = band.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && double.TryParse(parts[0], out _) && double.TryParse(parts[1], out _))
                    rowLabels.Add($"{parts[0]}-{parts[1]}");
            }
            if (rowLabels.Count == 0)
            {
                TempData["Error"] = "Row Bands must look like \"2-10, 10-25\".";
                return RedirectToAction(nameof(Matrix), new { approvalTypeId });
            }
        }
        else
        {
            rowLabels.Add(""); // no row axis -- a single implicit row, i.e. a 1-field matrix
        }

        var cells = new List<MatrixCellViewModel>();
        foreach (var rowLabel in rowLabels)
            foreach (var col in columns)
                cells.Add(new MatrixCellViewModel
                {
                    RowLabel = rowLabel,
                    ColumnValue = col,
                    Levels = Enumerable.Range(1, MatrixGridViewModel.MaxLevelsPerCell).Select(n => new LevelFormRow { LevelNo = n }).ToList()
                });

        var sharedCriteria = new List<CriteriaFormRow>();
        for (var i = 0; i < criteriaFieldKey.Count; i++)
            if (!string.IsNullOrWhiteSpace(criteriaFieldKey[i]) && !string.IsNullOrWhiteSpace(criteriaValue.ElementAtOrDefault(i)))
                sharedCriteria.Add(new CriteriaFormRow { FieldKey = criteriaFieldKey[i].Trim(), Operator = criteriaOperator.ElementAtOrDefault(i) ?? "=", ComparisonValue = criteriaValue[i] });

        var model = new MatrixGridViewModel
        {
            Config = new MatrixConfigViewModel
            {
                ApprovalTypeId = approvalTypeId, MatrixName = matrixName.Trim(), ColumnFieldKey = columnFieldKey.Trim(), ColumnValues = columnValues,
                RowFieldKey = rowFieldKey?.Trim(), RowBands = rowBands, Priority = priority, Active = active, Criteria = sharedCriteria
            },
            Cells = cells,
            Users = await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync()
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MatrixPreview(int approvalTypeId, string matrixName, string columnFieldKey, string? rowFieldKey,
        List<string>? criteriaFieldKey, List<string>? criteriaOperator, List<string>? criteriaValue,
        List<string>? cellRowLabel, List<string>? cellColumnValue,
        [FromForm] Dictionary<string, List<int>>? cellLevelApprovers)
    {
        criteriaFieldKey ??= []; criteriaOperator ??= []; criteriaValue ??= [];
        cellRowLabel ??= []; cellColumnValue ??= [];
        cellLevelApprovers ??= [];

        var type = await db.ApprovalTypes.FindAsync(approvalTypeId);
        var userNames = await db.AppUsers.ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        var sharedCriteriaSummaries = new List<string>();
        for (var i = 0; i < criteriaFieldKey.Count; i++)
            if (!string.IsNullOrWhiteSpace(criteriaFieldKey[i]) && !string.IsNullOrWhiteSpace(criteriaValue.ElementAtOrDefault(i)))
                sharedCriteriaSummaries.Add($"{criteriaFieldKey[i]} {criteriaOperator.ElementAtOrDefault(i)} {criteriaValue[i]}");

        var rules = new List<MatrixPreviewRule>();
        var skipped = 0;

        for (var ci = 0; ci < cellColumnValue.Count; ci++)
        {
            var levels = new List<(int, List<string>)>();
            for (var levelNo = 1; levelNo <= MatrixGridViewModel.MaxLevelsPerCell; levelNo++)
            {
                var approverIds = cellLevelApprovers.GetValueOrDefault($"{ci}_{levelNo}") ?? [];
                if (approverIds.Count == 0) continue;
                levels.Add((levelNo, approverIds.Select(id => userNames.GetValueOrDefault(id, $"User #{id}")).ToList()));
            }

            if (levels.Count == 0) { skipped++; continue; }

            var criteriaSummary = new List<string>(sharedCriteriaSummaries);
            var rowLabel = cellRowLabel.ElementAtOrDefault(ci) ?? "";
            if (!string.IsNullOrEmpty(rowLabel) && !string.IsNullOrWhiteSpace(rowFieldKey))
            {
                var bounds = rowLabel.Split('-');
                if (bounds.Length == 2)
                {
                    criteriaSummary.Add($"{rowFieldKey} >= {bounds[0]}");
                    criteriaSummary.Add($"{rowFieldKey} <= {bounds[1]}");
                }
            }
            criteriaSummary.Add($"{columnFieldKey} = {cellColumnValue[ci]}");

            var ruleName = string.IsNullOrEmpty(rowLabel) ? $"{matrixName} - {cellColumnValue[ci]}" : $"{matrixName} - {cellColumnValue[ci]} ({rowLabel})";
            rules.Add(new MatrixPreviewRule { RuleName = ruleName, CriteriaSummary = criteriaSummary, Levels = levels });
        }

        var model = new MatrixPreviewViewModel
        {
            ApprovalTypeId = approvalTypeId,
            MatrixName = matrixName,
            ApprovalTypeName = type?.Name ?? "",
            Rules = rules,
            SkippedEmptyCells = skipped
        };
        return View(model);
    }

    async Task<WorkflowVersion> GetOrCreateCurrentVersionAsync(int approvalTypeId)
    {
        var version = await db.WorkflowVersions.SingleOrDefaultAsync(v => v.ApprovalTypeId == approvalTypeId && v.IsCurrent);
        if (version is not null) return version;

        version = new WorkflowVersion { ApprovalTypeId = approvalTypeId, VersionNo = 1, IsCurrent = true };
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    // ---------- Bulk CSV import ----------
    // One row = one full rule (criteria + every level's approver). Built for maintaining a
    // large combination matrix (e.g. branch x sales channel x discount range) in a
    // spreadsheet instead of one screen at a time -- ported from the original Approval
    // engine's Rule Builder verbatim, including its Sales-Discount-shaped criteria columns
    // (dormant/harmless for a type like CR that doesn't use them).
    static readonly string[] ImportHeader =
    [
        "RuleName", "Priority", "Active", "Branch", "Company", "SalesChannel", "OrderType",
        "VehicleModel", "ModelYear", "VinNumber", "DiscountFrom", "DiscountTo", "ValidFrom", "ValidTo",
        "Level1Approver", "Level2Approver", "Level3Approver", "Level4Approver", "Level5Approver"
    ];

    [HttpGet]
    public async Task<IActionResult> Import(int approvalTypeId)
    {
        var types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
        ViewBag.ApprovalTypes = types;
        ViewBag.SelectedTypeId = approvalTypeId != 0 ? approvalTypeId : types.FirstOrDefault()?.Id ?? 0;
        return View();
    }

    [HttpGet]
    public IActionResult ImportTemplate()
    {
        var sample = string.Join(",", ImportHeader) + "\n" +
            "Riyadh Retail 0-5%,10,Y,Urobah - Riyadh,,Retail,,,,,,0,5,,,approver1@example.com,approver2@example.com,,,\n" +
            ",20,Y,,,,,,,,,5,,,,approver1@example.com,approver2@example.com,approver3@example.com,,\n";
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sample)).ToArray();
        return File(bytes, "text/csv", "routing-rules-template.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> ImportPreview(int approvalTypeId, IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose a CSV file first.";
            return RedirectToAction(nameof(Import), new { approvalTypeId });
        }

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8))
            content = await reader.ReadToEndAsync();

        var preview = await BuildPreviewAsync(approvalTypeId, content);
        preview.EncodedFile = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        preview.FileName = file.FileName;

        return View("ImportPreview", preview);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportConfirm(int approvalTypeId, string encodedFile, string mode)
    {
        string content;
        try { content = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedFile)); }
        catch
        {
            TempData["Error"] = "The uploaded file could not be re-read. Please upload it again.";
            return RedirectToAction(nameof(Import), new { approvalTypeId });
        }

        // Never trust the hidden round-tripped field blindly -- re-validate exactly as the
        // preview step did before writing anything.
        var preview = await BuildPreviewAsync(approvalTypeId, content);
        if (preview.ErrorCount > 0)
        {
            TempData["Error"] = $"{preview.ErrorCount} row(s) still have errors -- fix the file and re-upload. Nothing was changed.";
            preview.EncodedFile = encodedFile;
            return View("ImportPreview", preview);
        }

        var version = await GetOrCreateCurrentVersionAsync(approvalTypeId);

        if (mode == "replace")
        {
            var existingRules = await db.RoutingRules.Where(r => r.WorkflowVersionId == version.Id && r.Active).ToListAsync();
            foreach (var old in existingRules)
            {
                old.Active = false;
                if (!old.RuleName.EndsWith(" (superseded)")) old.RuleName += " (superseded)";
            }
            await db.SaveChangesAsync();
        }

        var inserted = 0;
        var updated = 0;
        foreach (var row in preview.Rows)
        {
            var existing = mode == "upsert"
                ? await db.RoutingRules.FirstOrDefaultAsync(r => r.WorkflowVersionId == version.Id && r.RuleName == row.RuleName)
                : null;

            RoutingRule rule;
            if (existing is not null)
            {
                rule = existing;
                updated++;
                var oldStepIds = await db.WorkflowSteps.Where(s => s.RoutingRuleId == rule.Id).Select(s => s.Id).ToListAsync();
                db.WorkflowStepApprovers.RemoveRange(db.WorkflowStepApprovers.Where(a => oldStepIds.Contains(a.WorkflowStepId)));
                await db.SaveChangesAsync();
                db.WorkflowSteps.RemoveRange(db.WorkflowSteps.Where(s => oldStepIds.Contains(s.Id)));
                db.RoutingRuleCriteria.RemoveRange(db.RoutingRuleCriteria.Where(c => c.RoutingRuleId == rule.Id));
            }
            else
            {
                rule = new RoutingRule { WorkflowVersionId = version.Id };
                db.RoutingRules.Add(rule);
                inserted++;
            }

            rule.RuleName = row.RuleName;
            rule.Priority = row.Priority;
            rule.Active = row.Active;
            await db.SaveChangesAsync();

            var sort = 0;
            void AddCriteria(string field, string? value, string op = "=")
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                db.RoutingRuleCriteria.Add(new RoutingRuleCriteria { RoutingRuleId = rule.Id, FieldKey = field, Operator = op, ComparisonValue = value, SortOrder = sort++ });
            }
            AddCriteria("branch", row.Branch);
            AddCriteria("company", row.Company);
            AddCriteria("salesChannel", row.SalesChannel);
            AddCriteria("orderType", row.OrderType);
            AddCriteria("vehicleModel", row.VehicleModel);
            AddCriteria("modelYear", row.ModelYear);
            AddCriteria("vin", row.VinNumber);
            AddCriteria("requestedDiscountPercent", row.DiscountFrom, ">=");
            AddCriteria("requestedDiscountPercent", row.DiscountTo, "<=");
            AddCriteria("requestDate", row.ValidFrom, ">=");
            AddCriteria("requestDate", row.ValidTo, "<=");

            var levelNo = 1;
            foreach (var (userId, _) in row.ResolvedApprovers)
            {
                var step = new WorkflowStep { WorkflowVersionId = version.Id, RoutingRuleId = rule.Id, LevelNo = levelNo++, Mode = "ANY_ONE" };
                db.WorkflowSteps.Add(step);
                await db.SaveChangesAsync();
                db.WorkflowStepApprovers.Add(new WorkflowStepApprover { WorkflowStepId = step.Id, UserId = userId });
            }
            await db.SaveChangesAsync();
        }

        TempData["Success"] = mode == "replace"
            ? $"Replaced all rules for this Approval Type -- {inserted} rule(s) loaded from the file."
            : $"Import complete -- {inserted} rule(s) added, {updated} rule(s) updated.";
        return RedirectToAction(nameof(Index), new { approvalTypeId });
    }

    async Task<RoutingRuleImportPreview> BuildPreviewAsync(int approvalTypeId, string csvContent)
    {
        var type = await db.ApprovalTypes.FindAsync(approvalTypeId);
        var version = await db.WorkflowVersions.SingleOrDefaultAsync(v => v.ApprovalTypeId == approvalTypeId && v.IsCurrent);
        var preview = new RoutingRuleImportPreview { ApprovalTypeId = approvalTypeId, ApprovalTypeName = type?.Name ?? "(unknown)" };

        var table = CsvParser.Parse(csvContent);
        if (table.Count < 2)
        {
            preview.Rows.Add(new RoutingRuleImportRow { RowNumber = 0, Errors = { "File has no data rows." } });
            return preview;
        }

        var header = table[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        string? Get(string[] cells, int idx) => idx >= 0 && idx < cells.Length && !string.IsNullOrWhiteSpace(cells[idx]) ? cells[idx].Trim() : null;

        var iRuleName = Col("RuleName"); var iPriority = Col("Priority"); var iActive = Col("Active");
        var iBranch = Col("Branch"); var iCompany = Col("Company"); var iChannel = Col("SalesChannel"); var iOrderType = Col("OrderType");
        var iModel = Col("VehicleModel"); var iYear = Col("ModelYear"); var iVin = Col("VinNumber");
        var iDiscFrom = Col("DiscountFrom"); var iDiscTo = Col("DiscountTo"); var iValidFrom = Col("ValidFrom"); var iValidTo = Col("ValidTo");
        var levelCols = new[] { Col("Level1Approver"), Col("Level2Approver"), Col("Level3Approver"), Col("Level4Approver"), Col("Level5Approver") };

        if (iPriority < 0 || iActive < 0)
        {
            preview.Rows.Add(new RoutingRuleImportRow { RowNumber = 0, Errors = { $"Header is missing required columns. Expected: {string.Join(",", ImportHeader)}" } });
            return preview;
        }

        var users = await db.AppUsers.Where(u => u.IsActive).ToListAsync();

        for (var r = 1; r < table.Count; r++)
        {
            var cells = table[r];
            var row = new RoutingRuleImportRow { RowNumber = r + 1 };

            row.Branch = Get(cells, iBranch);
            row.Company = Get(cells, iCompany);
            row.SalesChannel = Get(cells, iChannel);
            row.OrderType = Get(cells, iOrderType);
            row.VehicleModel = Get(cells, iModel);
            row.ModelYear = Get(cells, iYear);
            row.VinNumber = Get(cells, iVin);
            row.DiscountFrom = Get(cells, iDiscFrom);
            row.DiscountTo = Get(cells, iDiscTo);
            row.ValidFrom = Get(cells, iValidFrom);
            row.ValidTo = Get(cells, iValidTo);

            var ruleName = Get(cells, iRuleName);
            row.RuleName = ruleName ?? $"{row.Branch ?? "Any Branch"} | {row.SalesChannel ?? "Any Channel"} | {row.DiscountFrom ?? "0"}-{row.DiscountTo ?? "*"}%";

            var priorityText = Get(cells, iPriority);
            if (!int.TryParse(priorityText, out var priority))
                row.Errors.Add($"Priority '{priorityText}' is not a whole number.");
            row.Priority = priority;

            var activeText = Get(cells, iActive)?.ToUpperInvariant();
            row.Active = activeText is "Y" or "YES" or "TRUE" or "1" || activeText is null;
            if (activeText is not null && activeText is not ("Y" or "YES" or "TRUE" or "1" or "N" or "NO" or "FALSE" or "0"))
                row.Errors.Add($"Active '{activeText}' is not recognized (use Y/N).");

            if (row.DiscountFrom is not null && !decimal.TryParse(row.DiscountFrom, out _))
                row.Errors.Add($"DiscountFrom '{row.DiscountFrom}' is not numeric.");
            if (row.DiscountTo is not null && !decimal.TryParse(row.DiscountTo, out _))
                row.Errors.Add($"DiscountTo '{row.DiscountTo}' is not numeric.");
            if (row.DiscountFrom is not null && row.DiscountTo is not null &&
                decimal.TryParse(row.DiscountFrom, out var df) && decimal.TryParse(row.DiscountTo, out var dt) && df > dt)
                row.Errors.Add("DiscountFrom is greater than DiscountTo.");

            if (row.ValidFrom is not null && !DateTime.TryParse(row.ValidFrom, out _))
                row.Errors.Add($"ValidFrom '{row.ValidFrom}' is not a valid date.");
            if (row.ValidTo is not null && !DateTime.TryParse(row.ValidTo, out _))
                row.Errors.Add($"ValidTo '{row.ValidTo}' is not a valid date.");

            foreach (var levelCol in levelCols)
            {
                var email = Get(cells, levelCol);
                if (email is null) break;
                row.LevelApproverEmails.Add(email);
                var user = users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase) || string.Equals(u.UserName, email, StringComparison.OrdinalIgnoreCase));
                if (user is null)
                    row.Errors.Add($"No active user found matching '{email}'.");
                else
                    row.ResolvedApprovers.Add((user.Id, user.DisplayName));
            }
            if (row.LevelApproverEmails.Count == 0)
                row.Errors.Add("At least one approver (Level1Approver) is required.");

            if (version is null)
                row.Errors.Add("This Approval Type has no current Workflow Version -- save one rule manually first, or contact an admin.");

            row.CriteriaSummary = string.Join(" AND ", new[]
            {
                row.Branch is null ? null : $"branch={row.Branch}",
                row.Company is null ? null : $"company={row.Company}",
                row.SalesChannel is null ? null : $"salesChannel={row.SalesChannel}",
                row.OrderType is null ? null : $"orderType={row.OrderType}",
                row.VehicleModel is null ? null : $"vehicleModel={row.VehicleModel}",
                row.ModelYear is null ? null : $"modelYear={row.ModelYear}",
                row.VinNumber is null ? null : $"vin={row.VinNumber}",
                row.DiscountFrom is null ? null : $"discount>={row.DiscountFrom}",
                row.DiscountTo is null ? null : $"discount<={row.DiscountTo}",
                row.ValidFrom is null ? null : $"from {row.ValidFrom}",
                row.ValidTo is null ? null : $"to {row.ValidTo}"
            }.Where(x => x is not null)) is { Length: > 0 } summary ? summary : "(matches any)";

            preview.Rows.Add(row);
        }

        return preview;
    }
}
