using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

public sealed class HomeController(UnifiedDbContext db, NotificationQueue notificationQueue) : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Requests");

    [AllowAnonymous]
    public IActionResult Error() => View();

    [Authorize(Policy = "UnifiedAdmin")]
    public async Task<IActionResult> PpfMonitor(PpfMonitorFilter filter)
    {
        var model = await BuildPpfMonitorViewModel(filter);
        return View(model);
    }

    [Authorize(Policy = "UnifiedAdmin")]
    public async Task<IActionResult> PpfMonitorExport(PpfMonitorFilter filter)
    {
        var model = await BuildPpfMonitorViewModel(filter);
        var bytes = CsvHelper.ToCsvBytes(model.Rows,
            ["Request No.", "Approval Type", "Event", "Action Type", "Target", "Attempt", "Status", "Error", "Started At", "Finished At"],
            r => [r.RequestNumber, r.ApprovalTypeName, r.EventCode, r.ActionType, r.Target ?? "", r.AttemptNo.ToString(),
                  r.Status, r.ErrorMessage ?? "", r.StartedAt?.ToString("u") ?? "", r.FinishedAt?.ToString("u") ?? ""]);
        return File(bytes, "text/csv", $"ppf-monitor-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    async Task<PpfMonitorViewModel> BuildPpfMonitorViewModel(PpfMonitorFilter filter)
    {
        var types = await db.ApprovalTypes.ToDictionaryAsync(t => t.Id, t => t.Name);
        var requestInfo = await db.Requests.ToDictionaryAsync(r => r.Id, r => (r.RequestNumber, r.ApprovalTypeId));

        IQueryable<Core.Models.PostProcessingExecution> query = db.PostProcessingExecutions;
        if (filter.ActionType is { Length: > 0 }) query = query.Where(e => e.ActionType == filter.ActionType);
        if (filter.Status is { Length: > 0 }) query = query.Where(e => e.Status == filter.Status);
        if (filter.FromDate is not null) query = query.Where(e => e.CreatedAt >= filter.FromDate);
        if (filter.ToDate is not null) query = query.Where(e => e.CreatedAt < filter.ToDate.Value.AddDays(1));

        var executions = await query.OrderByDescending(e => e.CreatedAt).ToListAsync();
        var ruleEventCodes = await db.PostProcessingRules.ToDictionaryAsync(r => r.Id, r => (r.EventCode, r.ApprovalTypeId));

        var rows = executions.Select(e =>
        {
            var (requestNumber, approvalTypeId) = requestInfo.GetValueOrDefault(e.RequestId, ("(deleted)", 0));
            var (eventCode, ruleApprovalTypeId) = ruleEventCodes.GetValueOrDefault(e.PostProcessingRuleId, ("(deleted rule)", approvalTypeId));
            var effectiveTypeId = approvalTypeId == 0 ? ruleApprovalTypeId : approvalTypeId;
            return new PpfMonitorRow
            {
                Id = e.Id,
                RequestId = e.RequestId,
                RequestNumber = requestNumber,
                ApprovalTypeId = effectiveTypeId,
                ApprovalTypeName = types.GetValueOrDefault(effectiveTypeId, "(unknown)"),
                EventCode = eventCode,
                ActionType = e.ActionType,
                Target = e.Target,
                AttemptNo = e.AttemptNo,
                Status = e.Status,
                ErrorMessage = e.ErrorMessage,
                StartedAt = e.StartedAt,
                FinishedAt = e.FinishedAt
            };
        }).ToList();

        if (filter.RequestNumber is { Length: > 0 })
            rows = rows.Where(r => r.RequestNumber.Contains(filter.RequestNumber, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filter.ApprovalTypeId is not null)
            rows = rows.Where(r => r.ApprovalTypeId == filter.ApprovalTypeId.Value).ToList();
        if (filter.EventCode is { Length: > 0 })
            rows = rows.Where(r => r.EventCode == filter.EventCode).ToList();

        return new PpfMonitorViewModel
        {
            Filter = filter,
            TotalCount = rows.Count,
            SentCount = rows.Count(r => r.Status == "Sent"),
            FailedCount = rows.Count(r => r.Status == "Failed"),
            SkippedCount = rows.Count(r => r.Status == "Skipped"),
            Rows = SortPpfRows(rows, filter.Sort, filter.Dir),
            ApprovalTypes = (await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync()).Select(t => (t.Id, t.Name)).ToList(),
            EventCodes = PostProcessingRulesController.EventCodes.ToList(),
            ActionTypes = ["Email"],
            QueueStatus = notificationQueue.GetStatus()
        };
    }

    static List<PpfMonitorRow> SortPpfRows(List<PpfMonitorRow> rows, string? sort, string dir)
    {
        IOrderedEnumerable<PpfMonitorRow>? ordered = sort switch
        {
            "Request" => rows.OrderBy(r => r.RequestNumber),
            "ApprovalType" => rows.OrderBy(r => r.ApprovalTypeName),
            "Event" => rows.OrderBy(r => r.EventCode),
            "Action" => rows.OrderBy(r => r.ActionType),
            "Target" => rows.OrderBy(r => r.Target),
            "Attempt" => rows.OrderBy(r => r.AttemptNo),
            "Status" => rows.OrderBy(r => r.Status),
            "When" => rows.OrderBy(r => r.FinishedAt ?? r.StartedAt),
            _ => null
        };
        if (ordered is null) return rows;
        return (dir == "desc" ? ordered.Reverse() : ordered).ToList();
    }
}
