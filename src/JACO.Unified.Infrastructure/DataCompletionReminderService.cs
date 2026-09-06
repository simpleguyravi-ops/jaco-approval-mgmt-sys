using System.Net;
using System.Text;
using System.Text.Json;
using JACO.Unified.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JACO.Unified.Infrastructure;

// Runs one rule: find every Approved request of the rule's Approval Type missing any of
// its configured fields, and -- if any are found -- email the single fixed recipient one
// table listing them, each row linking straight to that request's Details page. Used both
// by the scheduler and by the admin's manual "Send Now" button, same code path either way.
public sealed class DataCompletionReminderService(UnifiedDbContext db, MailSender mailSender, IConfiguration configuration)
{
    public async Task<DataCompletionReminderRun> RunAsync(int ruleId, string triggeredBy, string? triggeredByUserName)
    {
        var rule = await db.DataCompletionReminderRules.FindAsync(ruleId);
        var type = rule is null ? null : await db.ApprovalTypes.FindAsync(rule.ApprovalTypeId);

        var run = new DataCompletionReminderRun
        {
            RuleId = ruleId,
            ApprovalTypeName = type?.Name ?? "(unknown)",
            RunAtUtc = DateTime.UtcNow,
            TriggeredBy = triggeredBy,
            TriggeredByUserName = triggeredByUserName,
            RecipientAddress = rule?.RecipientAddress
        };

        if (rule is null || type is null)
        {
            run.Status = "Failed";
            run.ErrorMessage = "Rule or Approval Type not found.";
        }
        else
        {
            var fieldKeys = JsonSerializer.Deserialize<List<string>>(rule.FieldKeysJson) ?? [];
            var fieldLabels = await db.WorkflowFields
                .Where(f => fieldKeys.Contains(f.FieldKey) && (f.ApprovalTypeId == rule.ApprovalTypeId || f.ApprovalTypeId == null))
                .ToDictionaryAsync(f => f.FieldKey, f => f.FieldLabel);

            var approved = await db.Requests.Where(r => r.ApprovalTypeId == rule.ApprovalTypeId && r.Status == "Approved").ToListAsync();
            var requestIds = approved.Select(r => r.Id).ToList();
            var approvedAtByRequest = await db.RequestActions
                .Where(a => requestIds.Contains(a.RequestId) && a.ActionCode == "Approve")
                .GroupBy(a => a.RequestId)
                .Select(g => new { RequestId = g.Key, ApprovedAt = g.Max(a => a.CreatedAt) })
                .ToDictionaryAsync(x => x.RequestId, x => x.ApprovedAt);

            var matches = new List<(Request Request, List<string> MissingLabels, DateTime ApprovedAt)>();
            foreach (var r in approved)
            {
                var data = MailMergeService.ExtractDataTokens(r.DataJson);
                var missing = fieldKeys
                    .Where(k => !data.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v))
                    .Select(k => fieldLabels.GetValueOrDefault(k, k))
                    .ToList();
                if (missing.Count > 0)
                    matches.Add((r, missing, approvedAtByRequest.GetValueOrDefault(r.Id, r.UpdatedAt)));
            }
            run.MatchingCount = matches.Count;

            if (matches.Count == 0)
            {
                run.Status = "Skipped";
                run.ErrorMessage = "No Approved requests are currently missing any of the configured fields.";
            }
            else if (string.IsNullOrWhiteSpace(rule.RecipientAddress))
            {
                run.Status = "Skipped";
                run.ErrorMessage = "No recipient address configured on the rule.";
            }
            else if (rule.MailTemplateId is not int templateId)
            {
                run.Status = "Failed";
                run.ErrorMessage = "Rule has no Mail Template configured.";
            }
            else
            {
                var template = await db.MailTemplates.FindAsync(templateId);
                if (template is null || !template.IsActive)
                {
                    run.Status = "Failed";
                    run.ErrorMessage = "Mail template not found or inactive.";
                }
                else
                {
                    var baseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:5004").TrimEnd('/');
                    var tableHtml = BuildTableHtml(baseUrl, matches);
                    var tokens = new Dictionary<string, string>
                    {
                        ["{{ApprovalTypeName}}"] = WebUtility.HtmlEncode(type.Name),
                        ["{{MatchingCount}}"] = matches.Count.ToString(),
                        ["{{TableRows}}"] = tableHtml,
                    };
                    var subject = Replace(template.Subject, tokens);
                    var body = Replace(template.BodyHtml, tokens);

                    var (sent, error) = await mailSender.SendAsync(rule.RecipientAddress, subject, body);
                    run.Subject = subject;
                    run.BodyHtml = body;
                    run.Status = sent ? "Sent" : (error == "Email disabled in configuration" ? "Skipped" : "Failed");
                    run.ErrorMessage = error;
                }
            }
        }

        db.DataCompletionReminderRuns.Add(run);
        if (rule is not null) rule.LastRunAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return run;
    }

    static string BuildTableHtml(string baseUrl, List<(Request Request, List<string> MissingLabels, DateTime ApprovedAt)> matches)
    {
        var sb = new StringBuilder();
        sb.Append("<table role=\"presentation\" style=\"width:100%;border-collapse:collapse;font-family:Arial,sans-serif;font-size:13px;\">");
        sb.Append("<tr>")
          .Append("<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #ddd;\">Request No.</th>")
          .Append("<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #ddd;\">Subject</th>")
          .Append("<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #ddd;\">Missing</th>")
          .Append("<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #ddd;\">Approved On</th>")
          .Append("</tr>");
        foreach (var (request, missingLabels, approvedAt) in matches.OrderBy(m => m.Request.RequestNumber))
        {
            sb.Append("<tr>")
              .Append("<td style=\"padding:6px 10px;border-bottom:1px solid #eee;\"><a href=\"").Append(baseUrl).Append("/Requests/Details/").Append(request.Id).Append("\">")
              .Append(WebUtility.HtmlEncode(request.RequestNumber)).Append("</a></td>")
              .Append("<td style=\"padding:6px 10px;border-bottom:1px solid #eee;\">").Append(WebUtility.HtmlEncode(request.Subject ?? "")).Append("</td>")
              .Append("<td style=\"padding:6px 10px;border-bottom:1px solid #eee;color:#b45309;\">").Append(WebUtility.HtmlEncode(string.Join(", ", missingLabels))).Append("</td>")
              .Append("<td style=\"padding:6px 10px;border-bottom:1px solid #eee;\">").Append(approvedAt.ToString("dd MMM yyyy")).Append("</td>")
              .Append("</tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    static string Replace(string text, Dictionary<string, string> tokens)
    {
        foreach (var (key, value) in tokens) text = text.Replace(key, value);
        return text;
    }
}
