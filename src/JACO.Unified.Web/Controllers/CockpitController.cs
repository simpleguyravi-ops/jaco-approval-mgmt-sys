using System.Text.Json;
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

    // Compliance floor for every "Clear Old Entries" action: an admin can request any date,
    // but nothing newer than this ever gets deleted, no matter what's typed or POSTed --
    // closes off the single most damaging mistake (picking today's date and wiping
    // everything, including active/recent data still relevant to an open request or a
    // pending review) without taking the ability away entirely. Only enforced when
    // SystemSettings.IsProduction is set (see SystemSettingsController) -- in Test mode this
    // resolves to "today," i.e. no real restriction, so development/QA isn't blocked by a
    // policy meant for live data.
    const int MinRetentionDays = 90;
    async Task<(bool IsProduction, DateTime MaxAllowedBeforeDate)> GetRetentionFloorAsync()
    {
        var isProd = (await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1))?.IsProduction ?? false;
        var floor = isProd ? DateTime.UtcNow.Date.AddDays(-MinRetentionDays) : DateTime.UtcNow.Date;
        return (isProd, floor);
    }

    // Snapshots every row a Clear action is about to remove into LogArchive before it's
    // deleted -- same DbContext, same SaveChangesAsync call as the delete right after this
    // returns, so archive-write and delete succeed or fail together. Recovery from a mistake
    // is then "download/restore this archive row," not "restore the whole database."
    void Archive<T>(string logType, DateTime beforeDate, List<T> rows)
    {
        db.LogArchives.Add(new LogArchive
        {
            LogType = logType,
            BeforeDate = beforeDate,
            EntryCount = rows.Count,
            ContentJson = JsonSerializer.Serialize(rows),
            ClearedByUserName = User.Identity?.Name,
            ClearedAt = DateTime.UtcNow
        });
    }

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
        var (isProd, floor) = await GetRetentionFloorAsync();
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "Routing Log", MaxAllowedBeforeDate = floor, IsProduction = isProd });
        var count = await db.RoutingLog.CountAsync(r => r.CreatedAt < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "Routing Log", BeforeDate = beforeDate.Value, MatchingCount = count, MaxAllowedBeforeDate = floor, IsProduction = isProd });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearRoutingLogConfirmed(DateTime beforeDate)
    {
        // Re-checked here, not just in the view -- a POST can be replayed/forged independent
        // of what the confirm page showed.
        var (_, floor) = await GetRetentionFloorAsync();
        if (beforeDate > floor)
        {
            TempData["Error"] = $"Entries newer than {MinRetentionDays} days can't be cleared (minimum retention policy).";
            return RedirectToAction(nameof(ClearRoutingLog));
        }
        var rows = await db.RoutingLog.Where(r => r.CreatedAt < beforeDate).ToListAsync();
        Archive("RoutingLog", beforeDate, rows);
        db.RoutingLog.RemoveRange(rows);
        db.AuditLogs.Add(new AuditLog { ActionCode = "RoutingLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{rows.Count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {rows.Count} routing log entries older than {beforeDate:dd MMM yyyy} (archived first -- see Archived Clears).";
        return RedirectToAction(nameof(RoutingLog));
    }

    [HttpGet]
    public async Task<IActionResult> ClearAuditLog(DateTime? beforeDate)
    {
        var (isProd, floor) = await GetRetentionFloorAsync();
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "Audit Log", MaxAllowedBeforeDate = floor, IsProduction = isProd });
        var count = await db.AuditLogs.CountAsync(a => a.CreatedAt < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "Audit Log", BeforeDate = beforeDate.Value, MatchingCount = count, MaxAllowedBeforeDate = floor, IsProduction = isProd });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAuditLogConfirmed(DateTime beforeDate)
    {
        var (_, floor) = await GetRetentionFloorAsync();
        if (beforeDate > floor)
        {
            TempData["Error"] = $"Entries newer than {MinRetentionDays} days can't be cleared (minimum retention policy).";
            return RedirectToAction(nameof(ClearAuditLog));
        }
        var rows = await db.AuditLogs.Where(a => a.CreatedAt < beforeDate).ToListAsync();
        Archive("AuditLog", beforeDate, rows);
        db.AuditLogs.RemoveRange(rows);
        db.AuditLogs.Add(new AuditLog { ActionCode = "AuditLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{rows.Count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {rows.Count} audit log entries older than {beforeDate:dd MMM yyyy} (archived first -- see Archived Clears).";
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
        var (isProd, floor) = await GetRetentionFloorAsync();
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "API Request Log", MaxAllowedBeforeDate = floor, IsProduction = isProd });
        var count = await db.ApiRequestLog.CountAsync(r => r.CreatedAt < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "API Request Log", BeforeDate = beforeDate.Value, MatchingCount = count, MaxAllowedBeforeDate = floor, IsProduction = isProd });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearApiRequestLogConfirmed(DateTime beforeDate)
    {
        var (_, floor) = await GetRetentionFloorAsync();
        if (beforeDate > floor)
        {
            TempData["Error"] = $"Entries newer than {MinRetentionDays} days can't be cleared (minimum retention policy).";
            return RedirectToAction(nameof(ClearApiRequestLog));
        }
        var rows = await db.ApiRequestLog.Where(r => r.CreatedAt < beforeDate).ToListAsync();
        Archive("ApiRequestLog", beforeDate, rows);
        db.ApiRequestLog.RemoveRange(rows);
        db.AuditLogs.Add(new AuditLog { ActionCode = "ApiRequestLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{rows.Count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {rows.Count} API request log entries older than {beforeDate:dd MMM yyyy} (archived first -- see Archived Clears).";
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
        var (isProd, floor) = await GetRetentionFloorAsync();
        if (beforeDate is null) return View(new ClearLogsResult { LogType = "Digest Log", MaxAllowedBeforeDate = floor, IsProduction = isProd });
        var count = await db.DigestRuns.CountAsync(r => r.RunAtUtc < beforeDate.Value);
        return View(new ClearLogsResult { LogType = "Digest Log", BeforeDate = beforeDate.Value, MatchingCount = count, MaxAllowedBeforeDate = floor, IsProduction = isProd });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearDigestLogConfirmed(DateTime beforeDate)
    {
        var (_, floor) = await GetRetentionFloorAsync();
        if (beforeDate > floor)
        {
            TempData["Error"] = $"Entries newer than {MinRetentionDays} days can't be cleared (minimum retention policy).";
            return RedirectToAction(nameof(ClearDigestLog));
        }
        var runs = await db.DigestRuns.Where(r => r.RunAtUtc < beforeDate).ToListAsync();
        var runIds = runs.Select(r => r.Id).ToList();
        var recipients = await db.DigestRunRecipients.Where(r => runIds.Contains(r.DigestRunId)).ToListAsync();
        // Both tables archived together as one snapshot -- a restored run without its
        // recipients (or vice versa) isn't useful evidence of what was actually sent.
        Archive("DigestLog", beforeDate, new List<object> { new { Runs = runs, Recipients = recipients } });
        db.DigestRunRecipients.RemoveRange(recipients);
        db.DigestRuns.RemoveRange(runs);
        db.AuditLogs.Add(new AuditLog { ActionCode = "DigestLogCleared", DetailsJson = $"{{\"beforeDate\":\"{beforeDate:O}\",\"rowsDeleted\":{runs.Count}}}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        TempData["Success"] = $"Cleared {runs.Count} digest run(s) older than {beforeDate:dd MMM yyyy} (archived first -- see Archived Clears).";
        return RedirectToAction(nameof(DigestLog));
    }

    // ---------- Archived Clears (recovery copies written by every Clear action above) ----------

    IQueryable<LogArchive> LogArchiveQuery(LogArchiveFilter filter)
    {
        var query = db.LogArchives.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.LogType)) query = query.Where(a => a.LogType == filter.LogType);

        var desc = filter.Dir != "asc";
        return filter.Sort switch
        {
            "LogType" => desc ? query.OrderByDescending(a => a.LogType) : query.OrderBy(a => a.LogType),
            "EntryCount" => desc ? query.OrderByDescending(a => a.EntryCount) : query.OrderBy(a => a.EntryCount),
            "ClearedBy" => desc ? query.OrderByDescending(a => a.ClearedByUserName) : query.OrderBy(a => a.ClearedByUserName),
            _ => desc ? query.OrderByDescending(a => a.ClearedAt) : query.OrderBy(a => a.ClearedAt)
        };
    }

    [HttpGet]
    public async Task<IActionResult> ArchivedClears(LogArchiveFilter filter)
    {
        var query = LogArchiveQuery(filter);
        ViewBag.Filter = filter;
        ViewBag.TotalCount = await query.CountAsync();
        ViewBag.PageSize = PageSize;
        return View(await query.Take(PageSize).ToListAsync());
    }

    [HttpGet]
    public async Task<IActionResult> DownloadLogArchive(long id)
    {
        var archive = await db.LogArchives.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
        if (archive is null) return NotFound();
        var bytes = System.Text.Encoding.UTF8.GetBytes(archive.ContentJson);
        return File(bytes, "application/json", $"{archive.LogType}-archive-{archive.ClearedAt:yyyyMMdd-HHmmss}.json");
    }
}
