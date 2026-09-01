using JACO.Unified.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Infrastructure;

public sealed record VolumeByType(int ApprovalTypeId, string TypeName, int Total, int Draft, int Pending, int Approved, int Rejected, int SentBack, int Withdrawn);
public sealed record VolumeTrendPoint(DateTime WeekStart, int Created, int Approved, int Rejected);
public sealed record VolumeReport(int Total, int Draft, int Pending, int Approved, int Rejected, int Withdrawn, List<VolumeByType> ByType, List<VolumeTrendPoint> Trend);

public sealed record CycleTimeByType(int ApprovalTypeId, string TypeName, int CompletedCount, double AvgHours, double MedianHours, double MinHours, double MaxHours);
public sealed record LevelTimeStat(int LevelNo, int Count, double AvgHours);
public sealed record CycleTimeReport(List<CycleTimeByType> ByType, List<LevelTimeStat> ByLevel);

public sealed record ApproverStat(int UserId, string DisplayName, string? Department, int ApprovedCount, int RejectedCount, int SentBackCount, double AvgDecisionHours, int CurrentPendingCount);
public sealed record ApproverWorkloadReport(List<ApproverStat> Approvers);

// Read-only analytics over the same Requests/RequestActions/AuditLogs rows the app already
// writes -- no new tracking tables. "Cycle time" and per-level timing are derived, not
// stored: a request's CURRENT cycle starts at its most recent Submit/Resubmit AuditLog
// (so time spent being edited after a Send Back doesn't count against the approval
// process), and a level's span runs from the previous level's last decision to this
// level's last decision. All in-memory once fetched -- this platform's request volumes
// don't warrant pushing the correlation into SQL.
public sealed class ReportsService(UnifiedDbContext db)
{
    static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    public async Task<VolumeReport> GetVolumeReportAsync(int? approvalTypeId, DateTime from, DateTime toExclusive)
    {
        var types = await db.ApprovalTypes.ToDictionaryAsync(t => t.Id, t => t.Name);

        var created = await db.Requests
            .Where(r => (approvalTypeId == null || r.ApprovalTypeId == approvalTypeId) && r.CreatedAt >= from && r.CreatedAt < toExclusive)
            .Select(r => new { r.ApprovalTypeId, r.Status, r.CreatedAt })
            .ToListAsync();

        int Count(string status) => created.Count(r => r.Status == status);

        var byType = created.GroupBy(r => r.ApprovalTypeId).Select(g => new VolumeByType(
            g.Key, types.GetValueOrDefault(g.Key, "(unknown)"),
            g.Count(),
            g.Count(r => r.Status == "Draft"),
            g.Count(r => r.Status is "Pending" or "Sent Back"),
            g.Count(r => r.Status == "Approved"),
            g.Count(r => r.Status == "Rejected"),
            g.Count(r => r.Status == "Sent Back"),
            g.Count(r => r.Status == "Withdrawn")
        )).OrderByDescending(x => x.Total).ToList();

        // Trend uses whichever date actually reflects the event: CreatedAt for the "raised"
        // series, UpdatedAt for the terminal-decision series -- a request decided this week
        // can easily have been raised weeks earlier, so bucketing everything off CreatedAt
        // would misrepresent when decisions actually landed.
        var decided = await db.Requests
            .Where(r => (approvalTypeId == null || r.ApprovalTypeId == approvalTypeId) && (r.Status == "Approved" || r.Status == "Rejected") && r.UpdatedAt >= from && r.UpdatedAt < toExclusive)
            .Select(r => new { r.Status, r.UpdatedAt })
            .ToListAsync();

        DateTime WeekStart(DateTime d) => d.Date.AddDays(-(int)d.DayOfWeek);
        var trend = created.Select(r => WeekStart(r.CreatedAt)).Concat(decided.Select(r => WeekStart(r.UpdatedAt))).Distinct().OrderBy(w => w)
            .Select(w => new VolumeTrendPoint(
                w,
                created.Count(r => WeekStart(r.CreatedAt) == w),
                decided.Count(r => WeekStart(r.UpdatedAt) == w && r.Status == "Approved"),
                decided.Count(r => WeekStart(r.UpdatedAt) == w && r.Status == "Rejected")
            )).ToList();

        return new VolumeReport(created.Count, Count("Draft"), created.Count(r => r.Status is "Pending" or "Sent Back"), Count("Approved"), Count("Rejected"), Count("Withdrawn"), byType, trend);
    }

    // Per-request: cycleStart = latest Submit/Resubmit AuditLog CreatedAt (the current
    // attempt only -- ignores anything before the most recent Send Back/resubmit). Level
    // spans chain off each other; a level's end is the latest decision CreatedAt within
    // that level (covers ALL/MAJORITY/MINIMUM_COUNT levels with more than one vote).
    async Task<(Dictionary<long, DateTime> cycleStart, Dictionary<long, List<RequestAction>> actionsByRequest)> BuildChronologyAsync(List<long> requestIds)
    {
        if (requestIds.Count == 0) return (new(), new());

        var submits = await db.AuditLogs
            .Where(a => a.RequestId != null && requestIds.Contains(a.RequestId.Value) && (a.ActionCode == "Submit" || a.ActionCode == "Resubmit"))
            .ToListAsync();
        var cycleStart = submits.GroupBy(a => a.RequestId!.Value).ToDictionary(g => g.Key, g => g.Max(a => a.CreatedAt));

        var actions = await db.RequestActions
            .Where(a => requestIds.Contains(a.RequestId) && (a.ActionCode == "Approve" || a.ActionCode == "Reject" || a.ActionCode == "SendBack"))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
        var actionsByRequest = actions.GroupBy(a => a.RequestId).ToDictionary(g => g.Key, g => g.ToList());

        return (cycleStart, actionsByRequest);
    }

    public async Task<CycleTimeReport> GetCycleTimeReportAsync(int? approvalTypeId, DateTime from, DateTime toExclusive)
    {
        var types = await db.ApprovalTypes.ToDictionaryAsync(t => t.Id, t => t.Name);

        var completed = await db.Requests
            .Where(r => (approvalTypeId == null || r.ApprovalTypeId == approvalTypeId) && (r.Status == "Approved" || r.Status == "Rejected") && r.UpdatedAt >= from && r.UpdatedAt < toExclusive)
            .ToListAsync();

        var (cycleStart, actionsByRequest) = await BuildChronologyAsync(completed.Select(r => r.Id).ToList());

        var cycleRows = new List<(int TypeId, double Hours)>();
        var levelRows = new List<(int LevelNo, double Hours)>();

        foreach (var r in completed)
        {
            if (!cycleStart.TryGetValue(r.Id, out var start)) continue;
            var relevant = (actionsByRequest.GetValueOrDefault(r.Id) ?? []).Where(a => a.CreatedAt >= start).OrderBy(a => a.CreatedAt).ToList();
            if (relevant.Count == 0) continue;

            cycleRows.Add((r.ApprovalTypeId, Math.Max(0, (r.UpdatedAt - start).TotalHours)));

            var levelStart = start;
            foreach (var levelGroup in relevant.GroupBy(a => a.LevelNo).OrderBy(g => g.Key))
            {
                var levelEnd = levelGroup.Max(a => a.CreatedAt);
                levelRows.Add((levelGroup.Key, Math.Max(0, (levelEnd - levelStart).TotalHours)));
                levelStart = levelEnd;
            }
        }

        var byType = cycleRows.GroupBy(x => x.TypeId).Select(g => new CycleTimeByType(
            g.Key, types.GetValueOrDefault(g.Key, "(unknown)"),
            g.Count(), g.Average(x => x.Hours), Median(g.Select(x => x.Hours)), g.Min(x => x.Hours), g.Max(x => x.Hours)
        )).OrderByDescending(x => x.CompletedCount).ToList();

        var byLevel = levelRows.GroupBy(x => x.LevelNo).Select(g => new LevelTimeStat(g.Key, g.Count(), g.Average(x => x.Hours))).OrderBy(x => x.LevelNo).ToList();

        return new CycleTimeReport(byType, byLevel);
    }

    public async Task<ApproverWorkloadReport> GetApproverWorkloadReportAsync(int? approvalTypeId, DateTime from, DateTime toExclusive)
    {
        IQueryable<RequestAction> windowQuery = db.RequestActions
            .Where(a => (a.ActionCode == "Approve" || a.ActionCode == "Reject" || a.ActionCode == "SendBack") && a.CreatedAt >= from && a.CreatedAt < toExclusive);
        if (approvalTypeId is not null)
        {
            var typeRequestIds = db.Requests.Where(r => r.ApprovalTypeId == approvalTypeId).Select(r => r.Id);
            windowQuery = windowQuery.Where(a => typeRequestIds.Contains(a.RequestId));
        }
        var windowActions = await windowQuery.ToListAsync();

        // Full (unfiltered) chronology for every touched request -- an in-window decision's
        // wait time depends on when its level started, which can be an out-of-window event.
        var touchedRequestIds = windowActions.Select(a => a.RequestId).Distinct().ToList();
        var (cycleStart, actionsByRequest) = await BuildChronologyAsync(touchedRequestIds);

        var decisionHours = new Dictionary<long, double>(); // RequestAction.Id -> hours since level start
        foreach (var requestId in touchedRequestIds)
        {
            if (!cycleStart.TryGetValue(requestId, out var start)) continue;
            var relevant = (actionsByRequest.GetValueOrDefault(requestId) ?? []).Where(a => a.CreatedAt >= start).OrderBy(a => a.CreatedAt).ToList();
            var levelStart = start;
            foreach (var levelGroup in relevant.GroupBy(a => a.LevelNo).OrderBy(g => g.Key))
            {
                var levelEnd = levelGroup.Max(a => a.CreatedAt);
                foreach (var action in levelGroup)
                    decisionHours[action.Id] = Math.Max(0, (action.CreatedAt - levelStart).TotalHours);
                levelStart = levelEnd;
            }
        }

        var users = await db.AppUsers.ToDictionaryAsync(u => u.Id, u => u);

        var pendingCounts = await GetCurrentPendingCountsPerApproverAsync(approvalTypeId);

        var stats = windowActions.GroupBy(a => a.UserId).Select(g =>
        {
            var user = users.GetValueOrDefault(g.Key);
            var hours = g.Where(a => decisionHours.ContainsKey(a.Id)).Select(a => decisionHours[a.Id]).ToList();
            return new ApproverStat(
                g.Key,
                user?.DisplayName ?? $"User #{g.Key}",
                user?.Department,
                g.Count(a => a.ActionCode == "Approve"),
                g.Count(a => a.ActionCode == "Reject"),
                g.Count(a => a.ActionCode == "SendBack"),
                hours.Count > 0 ? hours.Average() : 0,
                pendingCounts.GetValueOrDefault(g.Key, 0)
            );
        }).OrderByDescending(x => x.ApprovedCount + x.RejectedCount + x.SentBackCount).ToList();

        return new ApproverWorkloadReport(stats);
    }

    async Task<Dictionary<int, int>> GetCurrentPendingCountsPerApproverAsync(int? approvalTypeId)
    {
        var pendingRequests = await db.Requests
            .Where(r => r.Status == "Pending" && (approvalTypeId == null || r.ApprovalTypeId == approvalTypeId) && r.WorkflowVersionId != null && r.RoutingRuleId != null && r.CurrentLevelNo != null)
            .Select(r => new { r.WorkflowVersionId, r.RoutingRuleId, r.CurrentLevelNo })
            .ToListAsync();
        if (pendingRequests.Count == 0) return new();

        var stepKeys = pendingRequests.Select(r => (r.WorkflowVersionId!.Value, r.RoutingRuleId!.Value, r.CurrentLevelNo!.Value)).Distinct().ToHashSet();
        var candidateSteps = await db.WorkflowSteps
            .Where(s => pendingRequests.Select(r => r.WorkflowVersionId!.Value).Contains(s.WorkflowVersionId))
            .ToListAsync();
        var matchingStepIds = candidateSteps.Where(s => stepKeys.Contains((s.WorkflowVersionId, s.RoutingRuleId, s.LevelNo))).Select(s => s.Id).ToList();

        var approvers = await db.WorkflowStepApprovers.Where(a => matchingStepIds.Contains(a.WorkflowStepId)).ToListAsync();
        return approvers.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Count());
    }
}
