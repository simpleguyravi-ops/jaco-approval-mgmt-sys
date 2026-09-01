using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Ad-hoc "send me a table of everything pending" digest. Admin-triggered here; running it
// on a schedule is a separate, later piece of work (needs a scheduler, not just the
// rendering/sending capability).
[Authorize(Policy = "UnifiedAdmin")]
public sealed class DigestController(UnifiedDbContext db, RequestService requests, MailSender mailSender) : Controller
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
}
