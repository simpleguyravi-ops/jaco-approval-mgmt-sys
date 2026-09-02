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
                UserName = u != null ? u.DisplayName : null, ActionCode = a.ActionCode, DetailsJson = a.DetailsJson, Source = a.Source
            };

        if (!string.IsNullOrWhiteSpace(filter.RequestNumber)) query = query.Where(x => x.RequestNumber != null && x.RequestNumber.Contains(filter.RequestNumber));
        if (!string.IsNullOrWhiteSpace(filter.ActionCode)) query = query.Where(x => x.ActionCode == filter.ActionCode);
        if (!string.IsNullOrWhiteSpace(filter.Source)) query = query.Where(x => x.Source == filter.Source);
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
            ["Date/Time (UTC)", "Request No.", "User", "Action", "Source", "Detail"],
            r => [r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), r.RequestNumber ?? "", r.UserName ?? "", r.ActionCode, r.Source, r.DetailsJson ?? ""]);
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

    // ---------- API Request Log (external API's raw HTTP audit trail -- see ApiGatewayMiddleware) ----------

    IQueryable<ApiRequestLog> ApiRequestLogQuery(ApiRequestLogFilter filter)
    {
        var query = db.ApiRequestLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.ClientName)) query = query.Where(x => x.ClientName != null && x.ClientName.Contains(filter.ClientName));
        if (!string.IsNullOrWhiteSpace(filter.Path)) query = query.Where(x => x.Path.Contains(filter.Path));
        if (filter.StatusCode is not null) query = query.Where(x => x.StatusCode == filter.StatusCode);
        if (filter.DateFrom is not null) query = query.Where(x => x.CreatedAt >= filter.DateFrom.Value);
        if (filter.DateTo is not null) query = query.Where(x => x.CreatedAt < filter.DateTo.Value.AddDays(1));

        var desc = filter.Dir == "desc";
        return filter.Sort switch
        {
            "Client" => desc ? query.OrderByDescending(x => x.ClientName) : query.OrderBy(x => x.ClientName),
            "Path" => desc ? query.OrderByDescending(x => x.Path) : query.OrderBy(x => x.Path),
            "Status" => desc ? query.OrderByDescending(x => x.StatusCode) : query.OrderBy(x => x.StatusCode),
            "Duration" => desc ? query.OrderByDescending(x => x.DurationMs) : query.OrderBy(x => x.DurationMs),
            "CreatedAt" => desc ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            _ => query.OrderByDescending(x => x.CreatedAt)
        };
    }

    [HttpGet]
    public async Task<IActionResult> ApiRequestLog(ApiRequestLogFilter filter)
    {
        var query = ApiRequestLogQuery(filter);
        ViewBag.Filter = filter;
        ViewBag.TotalCount = await query.CountAsync();
        ViewBag.PageSize = PageSize;
        return View(await query.Take(PageSize).ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> ExportApiRequestLog(ApiRequestLogFilter filter)
    {
        var rows = await ApiRequestLogQuery(filter).Take(ExportCap).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(rows,
            ["Date/Time (UTC)", "Client", "Method", "Path", "Status", "Duration (ms)", "Remote IP"],
            r => [r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), r.ClientName ?? "(unauthenticated)", r.Method, r.Path, r.StatusCode.ToString(), r.DurationMs.ToString(), r.RemoteIp ?? ""]);
        return File(bytes, "text/csv", $"api-request-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet]
    public async Task<IActionResult> ClearApiRequestLog(DateTime? beforeDate)
    {
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "API Request Log" });
        var count = await db.ApiRequestLog.CountAsync(r => r.CreatedAt < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "API Request Log", BeforeDate = beforeDate.Value, MatchingCount = count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearApiRequestLogConfirmed(DateTime beforeDate)
    {
        var toDelete = db.ApiRequestLog.Where(r => r.CreatedAt < beforeDate);
        var count = await toDelete.CountAsync();
        db.ApiRequestLog.RemoveRange(toDelete);
        db.AuditLogs.Add(new AuditLog { ActionCode = "ApiRequestLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {count} API request log entries older than {beforeDate:dd MMM yyyy}.";
        return RedirectToAction(nameof(ApiRequestLog));
    }

    // ---------- Digest Log (automatic + manual Pending Approvals Digest runs -- see DigestService) ----------

    IQueryable<DigestRun> DigestRunQuery(DigestRunFilter filter)
    {
        var query = db.DigestRuns.AsNoTracking().AsQueryable();
        if (filter.ApprovalTypeId is not null) query = query.Where(r => r.ApprovalTypeId == filter.ApprovalTypeId);
        if (!string.IsNullOrWhiteSpace(filter.TriggeredBy)) query = query.Where(r => r.TriggeredBy == filter.TriggeredBy);
        if (filter.DateFrom is not null) query = query.Where(r => r.RunAtUtc >= filter.DateFrom.Value);
        if (filter.DateTo is not null) query = query.Where(r => r.RunAtUtc < filter.DateTo.Value.AddDays(1));

        var desc = filter.Dir == "desc";
        return filter.Sort switch
        {
            "Type" => desc ? query.OrderByDescending(r => r.ApprovalTypeName) : query.OrderBy(r => r.ApprovalTypeName),
            "Triggered" => desc ? query.OrderByDescending(r => r.TriggeredBy) : query.OrderBy(r => r.TriggeredBy),
            "Sent" => desc ? query.OrderByDescending(r => r.SentCount) : query.OrderBy(r => r.SentCount),
            "RunAt" => desc ? query.OrderByDescending(r => r.RunAtUtc) : query.OrderBy(r => r.RunAtUtc),
            _ => query.OrderByDescending(r => r.RunAtUtc)
        };
    }

    [HttpGet]
    public async Task<IActionResult> DigestLog(DigestRunFilter filter)
    {
        var query = DigestRunQuery(filter);
        ViewBag.Filter = filter;
        ViewBag.TotalCount = await query.CountAsync();
        ViewBag.PageSize = PageSize;
        ViewBag.Types = await db.ApprovalTypes.OrderBy(t => t.Name).ToListAsync();
        return View(await query.Take(PageSize).ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> ExportDigestLog(DigestRunFilter filter)
    {
        var rows = await DigestRunQuery(filter).Take(ExportCap).ToListAsync();
        var bytes = CsvHelper.ToCsvBytes(rows,
            ["Date/Time (UTC)", "Approval Type", "Triggered By", "Eligible Users", "Recipients (had pending)", "Sent", "Failed"],
            r => [r.RunAtUtc.ToString("yyyy-MM-dd HH:mm:ss"), r.ApprovalTypeName, r.TriggeredBy + (r.TriggeredByUserName is null ? "" : $" ({r.TriggeredByUserName})"), r.EligibleUserCount.ToString(), r.RecipientCount.ToString(), r.SentCount.ToString(), r.FailedCount.ToString()]);
        return File(bytes, "text/csv", $"digest-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    [HttpGet]
    public async Task<IActionResult> DigestRunDetail(long id)
    {
        var run = await db.DigestRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();
        ViewBag.Run = run;
        var recipients = await db.DigestRunRecipients.AsNoTracking().Where(r => r.DigestRunId == id).OrderBy(r => r.UserName).ToListAsync();
        return View(recipients);
    }

    [HttpGet]
    public async Task<IActionResult> ClearDigestLog(DateTime? beforeDate)
    {
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "Digest Log" });
        var count = await db.DigestRuns.CountAsync(r => r.RunAtUtc < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "Digest Log", BeforeDate = beforeDate.Value, MatchingCount = count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearDigestLogConfirmed(DateTime beforeDate)
    {
        var runIds = await db.DigestRuns.Where(r => r.RunAtUtc < beforeDate).Select(r => r.Id).ToListAsync();
        db.DigestRunRecipients.RemoveRange(db.DigestRunRecipients.Where(r => runIds.Contains(r.DigestRunId)));
        db.DigestRuns.RemoveRange(db.DigestRuns.Where(r => runIds.Contains(r.Id)));
        db.AuditLogs.Add(new AuditLog { ActionCode = "DigestLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{runIds.Count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {runIds.Count} digest run(s) older than {beforeDate:dd MMM yyyy}.";
        return RedirectToAction(nameof(DigestLog));
    }
}
