using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JACO.Unified.Infrastructure;

// Fires the "Email" action of any active PostProcessingRule configured for an
// ApprovalType + Event. Every attempt is recorded in PostProcessingExecutions regardless
// of outcome; a failure here never touches Request.Status.
public sealed class PpfExecutor(UnifiedDbContext db, MailSender mailSender, ApprovalActionLinkService linkService, TimelineService timelineService, IConfiguration configuration)
{
    public async Task RaiseEventAsync(long requestId, string eventCode)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return;

        var rules = await db.PostProcessingRules
            .Where(r => r.Active && r.ApprovalTypeId == request.ApprovalTypeId && r.EventCode == eventCode && r.ActionType == "Email")
            .OrderBy(r => r.SequenceNo)
            .ToListAsync();

        if (rules.Count == 0) return;

        var creator = await db.AppUsers.FindAsync(request.CreatorUserId);
        var creatorName = creator?.DisplayName ?? $"User #{request.CreatorUserId}";
        var baseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:5004").TrimEnd('/');
        // Same timeline HTML/logo for every recipient of this event -- built once, not
        // per-rule or per-recipient.
        var timelineHtml = BuildTimelineHtml(await timelineService.GetTimelineAsync(requestId));
        var logoUrl = $"{baseUrl}/img/jaco-logo-color.png";

        foreach (var rule in rules)
        {
            using var config = JsonDocument.Parse(rule.ActionConfigJson ?? "{}");
            var root = config.RootElement;
            var mailTemplateId = root.TryGetProperty("mailTemplateId", out var t) ? t.GetInt32() : (int?)null;
            var toMode = root.TryGetProperty("toMode", out var m) ? m.GetString() : "Creator";

            if (toMode == "CurrentApprover")
            {
                // One personalized email per current-level approver, never a single email
                // combined to everyone -- each recipient's Approve/Reject links are tokened
                // to THEM specifically, so they can't be a shared/forwardable "click to
                // approve" link that acts on behalf of whoever clicks it.
                var recipients = await GetCurrentApproverRecipientsAsync(request);
                if (recipients.Count == 0)
                {
                    await SendAndLogAsync(rule, request, requestId, mailTemplateId, creatorName, null, new Dictionary<string, string> { ["{{ApprovalTimeline}}"] = timelineHtml, ["{{LogoUrl}}"] = logoUrl });
                    continue;
                }
                foreach (var (userId, email) in recipients)
                {
                    var tokens = BuildActionTokens(request, userId, baseUrl);
                    tokens["{{ApprovalTimeline}}"] = timelineHtml;
                    tokens["{{LogoUrl}}"] = logoUrl;
                    await SendAndLogAsync(rule, request, requestId, mailTemplateId, creatorName, email, tokens);
                }
            }
            else
            {
                string? toAddress = toMode switch
                {
                    "Fixed" => root.TryGetProperty("toAddress", out var a) ? a.GetString() : null,
                    "Field" => root.TryGetProperty("toFieldKey", out var fk) ? RequestService.ExtractField(request.DataJson, fk.GetString() ?? "") : null,
                    _ => creator?.Email
                };
                // Not necessarily an approver (e.g. the creator getting a "Completed" email),
                // so only a plain login-required view link -- no Approve/Reject buttons that
                // would imply they're authorized to decide.
                var extraTokens = new Dictionary<string, string>
                {
                    ["{{RequestUrl}}"] = $"{baseUrl}/Requests/Details/{request.Id}",
                    ["{{ApprovalTimeline}}"] = timelineHtml,
                    ["{{LogoUrl}}"] = logoUrl,
                };
                await SendAndLogAsync(rule, request, requestId, mailTemplateId, creatorName, toAddress, extraTokens);
            }
        }

        await db.SaveChangesAsync();
    }

    async Task SendAndLogAsync(Core.Models.PostProcessingRule rule, Core.Models.Request request, long requestId, int? mailTemplateId, string creatorName, string? toAddress, IReadOnlyDictionary<string, string> extraTokens)
    {
        var startedAt = DateTime.UtcNow;
        var attemptNo = 1 + await db.PostProcessingExecutions
            .Where(e => e.PostProcessingRuleId == rule.Id && e.RequestId == requestId)
            .CountAsync();

        string status;
        string? error = null;

        try
        {
            if (mailTemplateId is null)
            {
                status = "Failed";
                error = "Rule has no mailTemplateId configured.";
            }
            else
            {
                var template = await db.MailTemplates.FindAsync(mailTemplateId.Value);
                if (template is null || !template.IsActive)
                {
                    status = "Failed";
                    error = "Mail template not found or inactive.";
                }
                else if (string.IsNullOrWhiteSpace(toAddress))
                {
                    status = "Skipped";
                    error = "No recipient address on file.";
                }
                else
                {
                    var (subject, body) = MailMergeService.RenderSingle(template, request, creatorName, extraTokens);
                    var (sent, sendError) = await mailSender.SendAsync(toAddress, subject, body);
                    status = sent ? "Sent" : sendError == "Email disabled in configuration" ? "Skipped" : "Failed";
                    error = sendError;
                }
            }
        }
        catch (Exception ex)
        {
            status = "Failed";
            error = ex.Message;
        }

        db.PostProcessingExecutions.Add(new Core.Models.PostProcessingExecution
        {
            PostProcessingRuleId = rule.Id,
            RequestId = requestId,
            AttemptNo = attemptNo,
            ActionType = rule.ActionType,
            Target = toAddress,
            Status = status,
            ErrorMessage = error,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            CreatedAt = startedAt
        });
    }

    // Inline-styled (email clients don't reliably load external/embedded <style> blocks) --
    // same statuses/colors as the Details page's timeline panel, just rendered as a static
    // HTML snippet instead of a live Razor partial.
    static string BuildTimelineHtml(List<TimelineLevel>? levels)
    {
        if (levels is null || levels.Count == 0) return "<p style=\"color:#6b7280;font-size:13px;\">No approval history yet.</p>";

        var sb = new System.Text.StringBuilder();
        sb.Append("<table role=\"presentation\" style=\"width:100%;border-collapse:collapse;font-family:Arial,sans-serif;\">");
        foreach (var level in levels)
        {
            var (dotColor, badgeBg, badgeColor, label) = level.LevelStatus switch
            {
                "Approved" => ("#15803d", "#dcfce7", "#15803d", "Approved"),
                "ActionRequired" => ("#f2600c", "#fef3c7", "#b45309", "Action Required"),
                "Rejected" => ("#b91c1c", "#fee2e2", "#b91c1c", "Rejected"),
                "SentBack" => ("#b45309", "#fef3c7", "#b45309", "Sent Back"),
                _ => ("#9ca3af", "#eef0f3", "#6b7280", "Not Started")
            };
            var approvers = level.ApproverNames.Count == 0 ? "-" : System.Net.WebUtility.HtmlEncode(string.Join(", ", level.ApproverNames));
            var note = level.LevelStatus switch
            {
                "ActionRequired" => "Waiting for a decision.",
                "NotStarted" => "Will be notified after the previous level is completed.",
                _ => level.Decisions.Count > 0 ? $"{System.Net.WebUtility.HtmlEncode(level.Decisions[^1].ActorName)} &middot; {level.Decisions[^1].AtUtc:dd MMM yyyy HH:mm}" : ""
            };

            sb.Append("<tr><td style=\"padding:0 0 16px;vertical-align:top;width:16px;\">")
              .Append($"<div style=\"width:12px;height:12px;border-radius:50%;background:{dotColor};margin-top:4px;\"></div>")
              .Append("</td><td style=\"padding:0 0 16px;\">")
              .Append($"<span style=\"font-weight:700;font-size:13px;color:#1f2937;\">Level {level.LevelNo}</span> ")
              .Append($"<span style=\"display:inline-block;padding:2px 8px;border-radius:999px;font-size:11px;font-weight:700;background:{badgeBg};color:{badgeColor};\">{label}</span>")
              .Append($"<div style=\"font-size:12.5px;color:#6b7280;margin-top:2px;\">{approvers}</div>")
              .Append(note.Length > 0 ? $"<div style=\"font-size:12px;color:#9ca3af;margin-top:2px;\">{note}</div>" : "")
              .Append("</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    Dictionary<string, string> BuildActionTokens(Core.Models.Request request, int userId, string baseUrl)
    {
        var requestUrl = $"{baseUrl}/Requests/Details/{request.Id}";
        var token = linkService.GenerateToken(request.Id, userId);
        var approveUrl = $"{baseUrl}/EmailAction/Decide?token={Uri.EscapeDataString(token)}&decision=Approve";
        var rejectUrl = $"{baseUrl}/EmailAction/RejectForm?token={Uri.EscapeDataString(token)}";
        return new Dictionary<string, string>
        {
            ["{{RequestUrl}}"] = requestUrl,
            ["{{ApproveUrl}}"] = approveUrl,
            ["{{RejectUrl}}"] = rejectUrl,
            ["{{ApproveButton}}"] = $"<a href=\"{approveUrl}\" style=\"display:inline-block;background:#15803d;color:#ffffff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-family:Arial,sans-serif;font-size:14px;\">Approve</a>",
            ["{{RejectButton}}"] = $"<a href=\"{rejectUrl}\" style=\"display:inline-block;background:#b91c1c;color:#ffffff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-family:Arial,sans-serif;font-size:14px;\">Reject</a>",
        };
    }

    async Task<List<(int UserId, string Email)>> GetCurrentApproverRecipientsAsync(Core.Models.Request request)
    {
        if (request.CurrentLevelNo is null || request.WorkflowVersionId is null || request.RoutingRuleId is null)
            return [];

        var step = await db.WorkflowSteps.SingleOrDefaultAsync(x =>
            x.WorkflowVersionId == request.WorkflowVersionId &&
            x.RoutingRuleId == request.RoutingRuleId &&
            x.LevelNo == request.CurrentLevelNo);
        if (step is null) return [];

        var approverIds = await db.WorkflowStepApprovers.Where(a => a.WorkflowStepId == step.Id).Select(a => a.UserId).ToListAsync();
        var users = await db.AppUsers.Where(u => approverIds.Contains(u.Id) && u.Email != null)
            .Select(u => new { u.Id, Email = u.Email! })
            .ToListAsync();
        return users.Select(u => (u.Id, u.Email)).ToList();
    }
}
