using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Operations cockpit: every routing/determination attempt (success or failure) and every
// decision-side action, filterable and clearable. Two different logs, two tabs -- the
// routing log answers "why didn't this route," the audit log answers "what happened to
// this request and when."
[Authorize(Policy = "UnifiedAdmin")]
public sealed class CockpitController(UnifiedDbContext db) : Controller
{
    const int PageSize = 200;
    const int ExportCap = 20000;

    public IActionResult Index() => RedirectToAction(nameof(RoutingLog));

    IQueryable<RoutingLogEntry> RoutingLogQuery(RoutingLogFilter filter)
    {
        var query = db.RoutingLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.RequestNumber)) query = query.Where(r => r.RequestNumber != null && r.RequestNumber.Contains(filter.RequestNumber));
        if (!string.IsNullOrWhiteSpace(filter.OutcomeCode)) query = query.Where(r => r.OutcomeCode == filter.OutcomeCode);
        if (filter.DateFrom is not null) query = query.Where(r => r.CreatedAt >= filter.DateFrom.Value);
        if (filter.DateTo is not null) query = query.Where(r => r.CreatedAt < filter.DateTo.Value.AddDays(1));

        var desc = filter.Dir == "desc";
        return filter.Sort switch
        {
            "Request" => desc ? query.OrderByDescending(r => r.RequestNumber) : query.OrderBy(r => r.RequestNumber),
            "Outcome" => desc ? query.OrderByDescending(r => r.OutcomeCode) : query.OrderBy(r => r.OutcomeCode),
            "MatchedRule" => desc ? query.OrderByDescending(r => r.MatchedRuleName) : query.OrderBy(r => r.MatchedRuleName),
            "CreatedAt" => desc ? query.OrderByDescending(r => r.CreatedAt) : query.OrderBy(r => r.CreatedAt),
            _ => query.OrderByDescending(r => r.CreatedAt)
        };
    }

    [HttpGet]
    public async Task<IActionResult> RoutingLog(RoutingLogFilter filter)
    {
        var query = RoutingLogQuery(filter);
        ViewBag.Filter = filter;
        ViewBag.TotalCount = await query.CountAsync();
        ViewBag.PageSize = PageSize;
        ViewBag.OutcomeCodes = new[] { "Routed", "NoRuleMatched", "NoApproversConfigured", "NoRulesConfigured" };
        return View(await query.Take(PageSize).ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> ExportRoutingLog(RoutingLogFilter filter)
    {
        var rows = await RoutingLogQuery(filter).Take(ExportCap).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(rows,
            ["Date/Time (UTC)", "Request No.", "Outcome", "Matched Rule", "Detail"],
            r => [r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), r.RequestNumber ?? "", r.OutcomeCode, r.MatchedRuleName ?? "", r.Detail ?? ""]);
        return File(bytes, "text/csv", $"routing-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    IQueryable<AuditLogRow> AuditLogQuery(AuditLogFilter filter)
    {
        var query =
            from a in db.AuditLogs.AsNoTracking()
            join r in db.Requests.AsNoTracking() on a.RequestId equals r.Id into rj
            from r in rj.DefaultIfEmpty()
            join u in db.AppUsers.AsNoTracking() on a.UserId equals u.Id into uj
            from u in uj.DefaultIfEmpty()
            select new AuditLogRow
            {
                Id = a.Id, CreatedAt = a.CreatedAt, RequestNumber = r != null ? r.RequestNumber : null,
                UserName = u != null ? u.DisplayName : null, ActionCode = a.ActionCode, DetailsJson = a.DetailsJson
            };

        if (!string.IsNullOrWhiteSpace(filter.RequestNumber)) query = query.Where(x => x.RequestNumber != null && x.RequestNumber.Contains(filter.RequestNumber));
        if (!string.IsNullOrWhiteSpace(filter.ActionCode)) query = query.Where(x => x.ActionCode == filter.ActionCode);
        if (filter.DateFrom is not null) query = query.Where(x => x.CreatedAt >= filter.DateFrom.Value);
        if (filter.DateTo is not null) query = query.Where(x => x.CreatedAt < filter.DateTo.Value.AddDays(1));
        if (filter.AdminOverrideOnly) query = query.Where(x => x.DetailsJson != null && x.DetailsJson.StartsWith(RequestService.AdminOverrideMarker));

        var desc = filter.Dir == "desc";
        return filter.Sort switch
        {
            "Request" => desc ? query.OrderByDescending(x => x.RequestNumber) : query.OrderBy(x => x.RequestNumber),
            "User" => desc ? query.OrderByDescending(x => x.UserName) : query.OrderBy(x => x.UserName),
            "Action" => desc ? query.OrderByDescending(x => x.ActionCode) : query.OrderBy(x => x.ActionCode),
            "CreatedAt" => desc ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            _ => query.OrderByDescending(x => x.CreatedAt)
        };
    }

    [HttpGet]
    public async Task<IActionResult> AuditLog(AuditLogFilter filter)
    {
        var query = AuditLogQuery(filter);
        ViewBag.Filter = filter;
        ViewBag.TotalCount = await query.CountAsync();
        ViewBag.PageSize = PageSize;
        ViewBag.ActionCodes = await db.AuditLogs.Select(a => a.ActionCode).Distinct().OrderBy(x => x).ToListAsync();
        return View(await query.Take(PageSize).ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> ExportAuditLog(AuditLogFilter filter)
    {
        var rows = await AuditLogQuery(filter).Take(ExportCap).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(rows,
            ["Date/Time (UTC)", "Request No.", "User", "Action", "Detail"],
            r => [r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), r.RequestNumber ?? "", r.UserName ?? "", r.ActionCode, r.DetailsJson ?? ""]);
        return File(bytes, "text/csv", $"audit-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet]
    public async Task<IActionResult> ClearRoutingLog(DateTime? beforeDate)
    {
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "Routing Log" });
        var count = await db.RoutingLog.CountAsync(r => r.CreatedAt < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "Routing Log", BeforeDate = beforeDate.Value, MatchingCount = count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearRoutingLogConfirmed(DateTime beforeDate)
    {
        var toDelete = db.RoutingLog.Where(r => r.CreatedAt < beforeDate);
        var count = await toDelete.CountAsync();
        db.RoutingLog.RemoveRange(toDelete);
        db.AuditLogs.Add(new AuditLog { ActionCode = "RoutingLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {count} routing log entries older than {beforeDate:dd MMM yyyy}.";
        return RedirectToAction(nameof(RoutingLog));
    }

    [HttpGet]
    public async Task<IActionResult> ClearAuditLog(DateTime? beforeDate)
    {
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "Audit Log" });
        var count = await db.AuditLogs.CountAsync(a => a.CreatedAt < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "Audit Log", BeforeDate = beforeDate.Value, MatchingCount = count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAuditLogConfirmed(DateTime beforeDate)
    {
        var toDelete = db.AuditLogs.Where(a => a.CreatedAt < beforeDate);
        var count = await toDelete.CountAsync();
        db.AuditLogs.RemoveRange(toDelete);
        db.AuditLogs.Add(new AuditLog { ActionCode = "AuditLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {count} audit log entries older than {beforeDate:dd MMM yyyy}.";
        return RedirectToAction(nameof(AuditLog));
    }
}
