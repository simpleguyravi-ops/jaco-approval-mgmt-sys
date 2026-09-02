using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Ad-hoc "send me a table of everything pending" digest (this controller's original
// purpose) PLUS the automatic per-Approval-Type schedule (Schedule/SaveSchedule/RunNow) --
// same MailMergeService.RenderTable rendering either way, just DigestService driving the
// scheduled/bulk path instead of one admin picking one recipient by hand.
[Authorize(Policy = "UnifiedAdmin")]
public sealed class DigestController(UnifiedDbContext db, RequestService requests, MailSender mailSender, DigestService digestService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index() => View(await BuildModel(null, null));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(int recipientUserId, int mailTemplateId)
    {
        var model = await BuildModel(recipientUserId, mailTemplateId);
        var recipient = await db.AppUsers.FindAsync(recipientUserId);
        var template = await db.MailTemplates.FindAsync(mailTemplateId);
        if (recipient is null || template is null) { model.ResultMessage = "Select a recipient and template."; return View("Index", model); }

        var pending = (await requests.GetMyWorkAsync(recipientUserId)).Where(x => x.Status == "Pending").ToList();
        model.PendingCount = pending.Count;
        var (subject, body) = MailMergeService.RenderTable(template, recipient.DisplayName, pending);
        model.PreviewSubject = subject;
        model.PreviewBody = body;
        return View("Index", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(int recipientUserId, int mailTemplateId)
    {
        var model = await BuildModel(recipientUserId, mailTemplateId);
        var recipient = await db.AppUsers.FindAsync(recipientUserId);
        var template = await db.MailTemplates.FindAsync(mailTemplateId);
        if (recipient is null || template is null) { model.ResultMessage = "Select a recipient and template."; return View("Index", model); }

        var pending = (await requests.GetMyWorkAsync(recipientUserId)).Where(x => x.Status == "Pending").ToList();
        model.PendingCount = pending.Count;
        var (subject, body) = MailMergeService.RenderTable(template, recipient.DisplayName, pending);
        model.PreviewSubject = subject;
        model.PreviewBody = body;

        var (sent, error) = await mailSender.SendAsync(recipient.Email ?? "", subject, body);
        model.ResultMessage = sent ? $"Digest sent to {recipient.Email}." : $"Not sent: {error}";
        return View("Index", model);
    }

    async Task<DigestViewModel> BuildModel(int? recipientUserId, int? mailTemplateId)
    {
        var users = await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync();
        var templates = await db.MailTemplates.Where(t => t.IsTableTemplate && t.IsActive).ToListAsync();

        return new DigestViewModel
        {
            RecipientUserId = recipientUserId,
            MailTemplateId = mailTemplateId,
            Recipients = users.Select(u => (u.Id, u.DisplayName)).ToList(),
            TableTemplates = templates.Select(t => (t.Id, t.Name)).ToList()
        };
    }

    // ---------- Automatic per-Approval-Type schedule ----------

    public static readonly (string Value, string Label)[] RecurrenceTypes =
    [
        ("EveryNDays", "Every N day(s)"),
        ("Weekdays", "Every working day (Mon-Fri)")
    ];

    [HttpGet]
    public async Task<IActionResult> Schedule(int approvalTypeId)
    {
        var types = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name).ToListAsync();
        if (approvalTypeId == 0) approvalTypeId = types.FirstOrDefault()?.Id ?? 0;
        return View(await BuildScheduleModel(approvalTypeId, types));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSchedule(int approvalTypeId, bool enabled, string recurrenceType, int intervalDays, string startTime, int? mailTemplateId)
    {
        if (enabled && mailTemplateId is null)
        {
            TempData["Error"] = "Pick a template before enabling the schedule.";
            return RedirectToAction(nameof(Schedule), new { approvalTypeId });
        }
        if (!TimeSpan.TryParse(startTime, out var parsedStartTime))
        {
            TempData["Error"] = "Invalid start time.";
            return RedirectToAction(nameof(Schedule), new { approvalTypeId });
        }

        var schedule = await db.DigestSchedules.SingleOrDefaultAsync(s => s.ApprovalTypeId == approvalTypeId);
        if (schedule is null)
        {
            schedule = new DigestSchedule { ApprovalTypeId = approvalTypeId };
            db.DigestSchedules.Add(schedule);
        }

        schedule.Enabled = enabled;
        schedule.RecurrenceType = recurrenceType == "Weekdays" ? "Weekdays" : "EveryNDays";
        schedule.IntervalDays = Math.Max(1, intervalDays);
        schedule.StartTime = parsedStartTime;
        schedule.MailTemplateId = mailTemplateId;
        schedule.UpdatedAt = DateTime.UtcNow;
        schedule.UpdatedByUserName = User.Identity?.Name;
        // Recomputed fresh from "now" whenever the schedule changes -- simpler and more
        // predictable than trying to preserve the old cadence's anchor across an edit.
        schedule.NextRunAtUtc = enabled ? DigestService.ComputeNextRunUtc(schedule.RecurrenceType, schedule.IntervalDays, schedule.StartTime, null) : null;

        await db.SaveChangesAsync();
        TempData["Success"] = "Schedule saved.";
        return RedirectToAction(nameof(Schedule), new { approvalTypeId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(int approvalTypeId)
    {
        var schedule = await db.DigestSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.ApprovalTypeId == approvalTypeId);
        if (schedule?.MailTemplateId is null)
        {
            TempData["Error"] = "Pick and save a template for this type before running it manually.";
            return RedirectToAction(nameof(Schedule), new { approvalTypeId });
        }

        var run = await digestService.RunDigestAsync(approvalTypeId, "Manual", User.Identity?.Name);
        TempData["Success"] = $"Digest run complete -- sent to {run.SentCount} of {run.RecipientCount} recipient(s) with pending items ({run.EligibleUserCount} active user(s) considered).{(run.FailedCount > 0 ? $" {run.FailedCount} failed." : "")}";
        return RedirectToAction(nameof(Schedule), new { approvalTypeId });
    }

    async Task<DigestScheduleViewModel> BuildScheduleModel(int approvalTypeId, List<ApprovalType> types)
    {
        var schedule = await db.DigestSchedules.SingleOrDefaultAsync(s => s.ApprovalTypeId == approvalTypeId)
            ?? new DigestSchedule { ApprovalTypeId = approvalTypeId };
        var templates = await db.MailTemplates.Where(t => t.IsTableTemplate && t.IsActive).ToListAsync();
        var lastRun = await db.DigestRuns.Where(r => r.ApprovalTypeId == approvalTypeId).OrderByDescending(r => r.RunAtUtc).FirstOrDefaultAsync();

        return new DigestScheduleViewModel
        {
            ApprovalTypeId = approvalTypeId,
            Types = types,
            Schedule = schedule,
            TableTemplates = templates.Select(t => (t.Id, t.Name)).ToList(),
            LastRun = lastRun
        };
    }
}
