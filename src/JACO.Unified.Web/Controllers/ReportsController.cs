using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Read-only reporting, deliberately its own top-level nav item rather than nested under
// Administration -- an Auditor grant (UserAccounts.IsAuditor) reaches exactly this and
// nothing else in Administration. Every action is type-agnostic: filtering by Approval
// Type is a query param, not separate code per type, so a future type gets reports for
// free the same way it already gets Create/Edit/Details for free.
[Authorize(Policy = "UnifiedReports")]
public sealed class ReportsController(UnifiedDbContext db, ReportsService reports) : Controller
{
    // A trailing, half-open [from, toExclusive) window -- "to" in the URL/UI is the last
    // INCLUDED day, so the exclusive bound is one day past it.
    (DateTime from, DateTime toExclusive, DateTime toInclusiveForDisplay) ResolveRange(DateTime? from, DateTime? to)
    {
        var toInclusive = (to ?? DateTime.UtcNow.Date).Date;
        var fromDate = (from ?? toInclusive.AddDays(-89)).Date;
        return (fromDate, toInclusive.AddDays(1), toInclusive);
    }

    async Task SetFilterViewBagAsync(int? approvalTypeId, DateTime from, DateTime toInclusive)
    {
        ViewBag.Types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
        ViewBag.SelectedTypeId = approvalTypeId;
        ViewBag.From = from;
        ViewBag.To = toInclusive;
    }

    public async Task<IActionResult> Index()
    {
        var (from, toExclusive, toInclusive) = ResolveRange(null, null);
        await SetFilterViewBagAsync(null, from, toInclusive);
        ViewBag.Volume = await reports.GetVolumeReportAsync(null, from, toExclusive);
        return View();
    }

    public async Task<IActionResult> Volume(int? approvalTypeId, DateTime? from, DateTime? to, string? sort, string dir = "asc")
    {
        var (f, tEx, tIn) = ResolveRange(from, to);
        await SetFilterViewBagAsync(approvalTypeId, f, tIn);
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var report = await reports.GetVolumeReportAsync(approvalTypeId, f, tEx);
        var desc = dir == "desc";
        IOrderedEnumerable<VolumeByType>? ordered = sort switch
        {
            "Type" => desc ? report.ByType.OrderByDescending(x => x.TypeName) : report.ByType.OrderBy(x => x.TypeName),
            "Total" => desc ? report.ByType.OrderByDescending(x => x.Total) : report.ByType.OrderBy(x => x.Total),
            "Draft" => desc ? report.ByType.OrderByDescending(x => x.Draft) : report.ByType.OrderBy(x => x.Draft),
            "Pending" => desc ? report.ByType.OrderByDescending(x => x.Pending) : report.ByType.OrderBy(x => x.Pending),
            "Approved" => desc ? report.ByType.OrderByDescending(x => x.Approved) : report.ByType.OrderBy(x => x.Approved),
            "Rejected" => desc ? report.ByType.OrderByDescending(x => x.Rejected) : report.ByType.OrderBy(x => x.Rejected),
            "Withdrawn" => desc ? report.ByType.OrderByDescending(x => x.Withdrawn) : report.ByType.OrderBy(x => x.Withdrawn),
            _ => null
        };
        if (ordered is not null) report = report with { ByType = ordered.ToList() };
        return View(report);
    }

    public async Task<IActionResult> VolumeExport(int? approvalTypeId, DateTime? from, DateTime? to)
    {
        var (f, tEx, _) = ResolveRange(from, to);
        var report = await reports.GetVolumeReportAsync(approvalTypeId, f, tEx);
        var bytes = CsvHelper.ToCsvBytes(report.ByType,
            ["Approval Type", "Total", "Draft", "Pending / Sent Back", "Approved", "Rejected", "Withdrawn"],
            r => [r.TypeName, r.Total.ToString(), r.Draft.ToString(), r.Pending.ToString(), r.Approved.ToString(), r.Rejected.ToString(), r.Withdrawn.ToString()]);
        return File(bytes, "text/csv", $"volume-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    public async Task<IActionResult> CycleTime(int? approvalTypeId, DateTime? from, DateTime? to, string? sort, string dir = "asc")
    {
        var (f, tEx, tIn) = ResolveRange(from, to);
        await SetFilterViewBagAsync(approvalTypeId, f, tIn);
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var report = await reports.GetCycleTimeReportAsync(approvalTypeId, f, tEx);
        var desc = dir == "desc";
        IOrderedEnumerable<CycleTimeByType>? ordered = sort switch
        {
            "Type" => desc ? report.ByType.OrderByDescending(x => x.TypeName) : report.ByType.OrderBy(x => x.TypeName),
            "Completed" => desc ? report.ByType.OrderByDescending(x => x.CompletedCount) : report.ByType.OrderBy(x => x.CompletedCount),
            "Avg" => desc ? report.ByType.OrderByDescending(x => x.AvgHours) : report.ByType.OrderBy(x => x.AvgHours),
            "Median" => desc ? report.ByType.OrderByDescending(x => x.MedianHours) : report.ByType.OrderBy(x => x.MedianHours),
            "Min" => desc ? report.ByType.OrderByDescending(x => x.MinHours) : report.ByType.OrderBy(x => x.MinHours),
            "Max" => desc ? report.ByType.OrderByDescending(x => x.MaxHours) : report.ByType.OrderBy(x => x.MaxHours),
            _ => null
        };
        if (ordered is not null) report = report with { ByType = ordered.ToList() };
        return View(report);
    }

    public async Task<IActionResult> CycleTimeExport(int? approvalTypeId, DateTime? from, DateTime? to)
    {
        var (f, tEx, _) = ResolveRange(from, to);
        var report = await reports.GetCycleTimeReportAsync(approvalTypeId, f, tEx);
        var bytes = CsvHelper.ToCsvBytes(report.ByType,
            ["Approval Type", "Completed", "Avg Hours", "Median Hours", "Min Hours", "Max Hours"],
            r => [r.TypeName, r.CompletedCount.ToString(), r.AvgHours.ToString("F1"), r.MedianHours.ToString("F1"), r.MinHours.ToString("F1"), r.MaxHours.ToString("F1")]);
        return File(bytes, "text/csv", $"cycle-time-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    public async Task<IActionResult> Approvers(int? approvalTypeId, DateTime? from, DateTime? to, string? sort, string dir = "asc")
    {
        var (f, tEx, tIn) = ResolveRange(from, to);
        await SetFilterViewBagAsync(approvalTypeId, f, tIn);
        ViewBag.Sort = sort; ViewBag.Dir = dir;
        var report = await reports.GetApproverWorkloadReportAsync(approvalTypeId, f, tEx);
        var desc = dir == "desc";
        IOrderedEnumerable<ApproverStat>? ordered = sort switch
        {
            "Approver" => desc ? report.Approvers.OrderByDescending(x => x.DisplayName) : report.Approvers.OrderBy(x => x.DisplayName),
            "Department" => desc ? report.Approvers.OrderByDescending(x => x.Department) : report.Approvers.OrderBy(x => x.Department),
            "Approved" => desc ? report.Approvers.OrderByDescending(x => x.ApprovedCount) : report.Approvers.OrderBy(x => x.ApprovedCount),
            "Rejected" => desc ? report.Approvers.OrderByDescending(x => x.RejectedCount) : report.Approvers.OrderBy(x => x.RejectedCount),
            "SentBack" => desc ? report.Approvers.OrderByDescending(x => x.SentBackCount) : report.Approvers.OrderBy(x => x.SentBackCount),
            "AvgHours" => desc ? report.Approvers.OrderByDescending(x => x.AvgDecisionHours) : report.Approvers.OrderBy(x => x.AvgDecisionHours),
            "Pending" => desc ? report.Approvers.OrderByDescending(x => x.CurrentPendingCount) : report.Approvers.OrderBy(x => x.CurrentPendingCount),
            _ => null
        };
        if (ordered is not null) report = report with { Approvers = ordered.ToList() };
        return View(report);
    }

    public async Task<IActionResult> ApproversExport(int? approvalTypeId, DateTime? from, DateTime? to)
    {
        var (f, tEx, _) = ResolveRange(from, to);
        var report = await reports.GetApproverWorkloadReportAsync(approvalTypeId, f, tEx);
        var bytes = CsvHelper.ToCsvBytes(report.Approvers,
            ["Approver", "Department", "Approved", "Rejected", "Sent Back", "Avg Decision Hours", "Currently Pending"],
            r => [r.DisplayName, r.Department ?? "", r.ApprovedCount.ToString(), r.RejectedCount.ToString(), r.SentBackCount.ToString(), r.AvgDecisionHours.ToString("F1"), r.CurrentPendingCount.ToString()]);
        return File(bytes, "text/csv", $"approver-workload-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }
}
