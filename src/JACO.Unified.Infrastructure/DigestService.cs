using JACO.Unified.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Infrastructure;

// Runs one Approval Type's Pending Approvals Digest -- every active user with an email
// address who currently has at least one item awaiting THEIR decision (the precise
// per-level check, GetPendingForUserAsync, not the looser "any pending request I'm
// involved in" the existing ad-hoc Digest screen uses) gets their own personalized email,
// scoped to only that type. Used both by the scheduler (DigestSchedulerHostedService) and
// by the admin's manual "Run Now" button -- same code path either way, so a manual run is
// a true test of what the schedule would actually do.
public sealed class DigestService(UnifiedDbContext db, RequestService requests, MailSender mailSender)
{
    public async Task<DigestRun> RunDigestAsync(int approvalTypeId, string triggeredBy, string? triggeredByUserName)
    {
        var type = await db.ApprovalTypes.FindAsync(approvalTypeId);
        var schedule = await db.DigestSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.ApprovalTypeId == approvalTypeId);
        var template = schedule?.MailTemplateId is int tid ? await db.MailTemplates.FindAsync(tid) : null;

        var run = new DigestRun
        {
            ApprovalTypeId = approvalTypeId,
            ApprovalTypeName = type?.Name ?? "(unknown)",
            RunAtUtc = DateTime.UtcNow,
            TriggeredBy = triggeredBy,
            TriggeredByUserName = triggeredByUserName
        };
        db.DigestRuns.Add(run);
        await db.SaveChangesAsync();

        var users = await db.AppUsers.Where(u => u.IsActive && u.Email != null && u.Email != "").ToListAsync();
        run.EligibleUserCount = users.Count;

        if (template is not null)
        {
            foreach (var user in users)
            {
                var mine = (await requests.GetMyWorkAsync(user.Id)).Where(r => r.ApprovalTypeId == approvalTypeId).ToList();
                var pendingIds = await requests.GetPendingForUserAsync(mine, user.Id);
                if (pendingIds.Count == 0) continue;

                var pendingRequests = mine.Where(r => pendingIds.Contains(r.Id)).ToList();
                var (subject, body) = MailMergeService.RenderTable(template, user.DisplayName, pendingRequests);
                var (sent, error) = await mailSender.SendAsync(user.Email!, subject, body);

                db.DigestRunRecipients.Add(new DigestRunRecipient
                {
                    DigestRunId = run.Id,
                    UserId = user.Id,
                    UserName = user.DisplayName,
                    Email = user.Email,
                    PendingCount = pendingRequests.Count,
                    Subject = subject,
                    BodyHtml = body,
                    Status = sent ? "Sent" : "Failed",
                    ErrorMessage = error
                });
                run.RecipientCount++;
                if (sent) run.SentCount++; else run.FailedCount++;
            }
        }

        await db.SaveChangesAsync();
        return run;
    }

    // "EveryNDays": steps IntervalDays at a time from the last run's calendar date (or from
    // today, for a never-yet-run schedule), always landing on StartTime and always in the
    // future -- if the app was down past several intervals, this lands on the correct next
    // occurrence directly rather than firing once per missed interval.
    // "Weekdays": same idea, stepping one day at a time and skipping Saturday/Sunday.
    public static DateTime ComputeNextRunUtc(string recurrenceType, int intervalDays, TimeSpan startTimeLocal, DateTime? lastRunUtc)
    {
        var nowLocal = DateTime.Now;
        // IntervalDays is meaningless in Weekdays mode -- always step 1 day at a time there
        // regardless of whatever value happens to be stored (e.g. left over from a previous
        // EveryNDays configuration), so a stale value can't silently skip a working day.
        var stepDays = recurrenceType == "Weekdays" ? 1 : Math.Max(1, intervalDays);

        var candidate = lastRunUtc is null
            ? nowLocal.Date + startTimeLocal
            : lastRunUtc.Value.ToLocalTime().Date.AddDays(stepDays) + startTimeLocal;

        while (candidate <= nowLocal) candidate = candidate.AddDays(stepDays);
        if (recurrenceType == "Weekdays")
            while (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) candidate = candidate.AddDays(1);

        return DateTime.SpecifyKind(candidate, DateTimeKind.Local).ToUniversalTime();
    }
}
