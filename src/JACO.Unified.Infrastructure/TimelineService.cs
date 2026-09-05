using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Infrastructure;

// Extracted out of RequestService (which now just delegates to this) so PpfExecutor can
// render the same Approval Timeline into emails without creating a circular dependency --
// RequestService already depends on PpfExecutor, so PpfExecutor can't depend back on
// RequestService. This has no dependency on either, so both can use it safely.
public sealed class TimelineService(UnifiedDbContext db)
{
    public async Task<List<TimelineLevel>?> GetTimelineAsync(long requestId)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null || request.WorkflowVersionId is null || request.RoutingRuleId is null) return null;

        var steps = await db.WorkflowSteps.Where(s => s.WorkflowVersionId == request.WorkflowVersionId && s.RoutingRuleId == request.RoutingRuleId).OrderBy(s => s.LevelNo).ToListAsync();
        var actions = await db.RequestActions.Where(a => a.RequestId == requestId).ToListAsync();

        // Batched once for every step instead of two queries (approver ids, then their
        // names) per step inside the loop below -- this runs synchronously on every
        // Submit/Decide (PpfExecutor builds the timeline HTML for the notification email),
        // so a workflow with several levels meant that many round trips per click.
        var stepIds = steps.Select(s => s.Id).ToList();
        var stepApprovers = await db.WorkflowStepApprovers.Where(a => stepIds.Contains(a.WorkflowStepId)).ToListAsync();
        var userIds = actions.Select(a => a.UserId).Concat(stepApprovers.Select(a => a.UserId)).Distinct().ToList();
        var userNames = await db.AppUsers.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        var result = new List<TimelineLevel>();
        foreach (var step in steps)
        {
            var approverNames = stepApprovers.Where(a => a.WorkflowStepId == step.Id)
                .Select(a => userNames.GetValueOrDefault(a.UserId)).Where(n => n is not null).Select(n => n!).ToList();
            var decisions = actions.Where(a => a.LevelNo == step.LevelNo && a.ActionCode is not ("Nudge" or "NoLongerRequired"))
                .Select(a => new TimelineDecision(userNames.GetValueOrDefault(a.UserId, $"User #{a.UserId}"), a.ActionCode, a.Comments, a.CreatedAt))
                .OrderBy(d => d.AtUtc).ToList();

            // Current state wins over history: a level that was once Sent Back but is now
            // back in the same approver's queue after resubmission must read as
            // ActionRequired again, not stuck showing its last historical decision.
            string levelStatus =
                request.CurrentLevelNo == step.LevelNo && request.Status == "Pending" ? "ActionRequired" :
                decisions.Any(d => d.ActionCode == "Reject") ? "Rejected" :
                decisions.Any(d => d.ActionCode == "SendBack") && !((request.CurrentLevelNo is not null && request.CurrentLevelNo > step.LevelNo) || request.Status is "Approved") ? "SentBack" :
                (request.CurrentLevelNo is not null && request.CurrentLevelNo > step.LevelNo) || request.Status is "Approved" ? "Approved" :
                decisions.Any(d => d.ActionCode == "Approve") ? "Approved" :
                "NotStarted";

            result.Add(new TimelineLevel(step.LevelNo, step.Mode, approverNames, decisions, levelStatus));
        }
        return result;
    }
}
