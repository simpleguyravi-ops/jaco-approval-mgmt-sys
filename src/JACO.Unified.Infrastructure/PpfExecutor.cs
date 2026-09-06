using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;

namespace JACO.Unified.Infrastructure;

// Fires the Email or ApiCall action of any active PostProcessingRule configured for one of
// the request's Approval Type + this Event. Every attempt is recorded in
// PostProcessingExecutions regardless of outcome; a failure here never touches
// Request.Status.
public sealed class PpfExecutor(UnifiedDbContext db, MailSender mailSender, ApprovalActionLinkService linkService, TimelineService timelineService, IConfiguration configuration, RequestAttachmentStorage attachmentStorage, IHttpClientFactory httpClientFactory)
{
    public async Task RaiseEventAsync(long requestId, string eventCode, int triggeredByUserId)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return;

        var rules = await db.PostProcessingRules
            .Where(r => r.Active && r.EventCode == eventCode && (r.ActionType == "Email" || r.ActionType == "ApiCall")
                && db.PostProcessingRuleApprovalTypes.Any(pat => pat.PostProcessingRuleId == r.Id && pat.ApprovalTypeId == request.ApprovalTypeId))
            .OrderBy(r => r.SequenceNo)
            .ToListAsync();

        if (rules.Count == 0) return;

        var type = await db.ApprovalTypes.FindAsync(request.ApprovalTypeId);
        var creator = await db.AppUsers.FindAsync(request.CreatorUserId);
        var creatorName = creator?.DisplayName ?? $"User #{request.CreatorUserId}";
        // Whoever's action caused THIS firing -- the submitter, the decider, the nudger.
        // Feeds both the "Decision-Triggered User" email recipient mode and the
        // {{TriggeredByUserName}}/payload field available regardless of action type.
        var triggeredByUser = await db.AppUsers.FindAsync(triggeredByUserId);
        var triggeredByUserName = triggeredByUser?.DisplayName ?? $"User #{triggeredByUserId}";
        var baseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:5004").TrimEnd('/');
        // Same timeline HTML/logo for every recipient of this event -- built once, not
        // per-rule or per-recipient.
        var timelineHtml = BuildTimelineHtml(await timelineService.GetTimelineAsync(requestId));
        // "cid:jaco-logo" -- an inline attachment MailSender embeds when it sees this,
        // not a URL. An http(s) URL built from AppBaseUrl only ever resolves on the
        // machine running this app, so a real recipient's own mail client just shows a
        // broken image; embedding travels the actual file with the email instead.
        var logoUrl = "cid:jaco-logo";
        // Built once and reused across every rule for this event -- an "Approved" firing
        // with 3 rules attaching files shouldn't re-hit the DB and re-resolve disk paths 3
        // times for the same, unchanging set of files.
        List<AttachmentFile>? requestAttachments = null;

        // Batched once for every candidate rule, same reasoning as RoutingService.ResolveAsync
        // batching RoutingRuleCriteria -- an event with several PPF rules shouldn't mean that
        // many extra round trips just to check whether each one's criteria match.
        var ruleIds = rules.Select(r => r.Id).ToList();
        var criteriaByRule = (await db.PostProcessingRuleCriteria.Where(c => ruleIds.Contains(c.PostProcessingRuleId)).ToListAsync())
            .GroupBy(c => c.PostProcessingRuleId).ToDictionary(g => g.Key, g => g.ToList());
        var routingContext = BuildRoutingContext(request.DataJson);

        foreach (var rule in rules)
        {
            var criteria = criteriaByRule.GetValueOrDefault(rule.Id, []);
            if (!RoutingService.EvaluateAll(criteria, routingContext))
            {
                await LogSkippedAsync(rule, requestId, "Rule's criteria did not match this request's submitted data.");
                continue;
            }

            using var config = JsonDocument.Parse(rule.ActionConfigJson ?? "{}");
            var root = config.RootElement;

            if (rule.ActionType == "ApiCall")
            {
                await CallApiAndLogAsync(rule, request, requestId, root, type?.Name ?? "(unknown)", creatorName, eventCode, triggeredByUserName);
                continue;
            }

            var mailTemplateId = root.TryGetProperty("mailTemplateId", out var t) ? t.GetInt32() : (int?)null;
            var toMode = root.TryGetProperty("toMode", out var m) ? m.GetString() : "Creator";
            var includeAttachments = root.TryGetProperty("includeAttachments", out var ia) && ia.ValueKind == JsonValueKind.True;
            var ccMode = root.TryGetProperty("ccMode", out var cm) ? cm.GetString() : "None";
            string? ccAddress = ccMode switch
            {
                "Fixed" => root.TryGetProperty("ccAddress", out var ca) ? ca.GetString() : null,
                "Field" => root.TryGetProperty("ccFieldKey", out var cfk) ? RequestService.ExtractField(request.DataJson, cfk.GetString() ?? "") : null,
                _ => null
            };

            IReadOnlyList<AttachmentFile>? attachmentsForThisRule = null;
            if (includeAttachments)
            {
                if (requestAttachments is null)
                {
                    var rows = await db.RequestAttachments.Where(a => a.RequestId == requestId).ToListAsync();
                    requestAttachments = rows.Select(a => new AttachmentFile(a.OriginalFileName, attachmentStorage.GetPath(a.RequestId, a.StoredFileName), a.ContentType)).ToList();
                }
                attachmentsForThisRule = requestAttachments;
            }

            if (toMode == "CurrentApprover")
            {
                // One personalized email per current-level approver, never a single email
                // combined to everyone -- each recipient's Approve/Reject links are tokened
                // to THEM specifically, so they can't be a shared/forwardable "click to
                // approve" link that acts on behalf of whoever clicks it.
                var recipients = await GetCurrentApproverRecipientsAsync(request);
                if (recipients.Count == 0)
                {
                    await SendAndLogAsync(rule, request, requestId, mailTemplateId, creatorName, null, ccAddress, new Dictionary<string, string> { ["{{ApprovalTimeline}}"] = timelineHtml, ["{{LogoUrl}}"] = logoUrl, ["{{TriggeredByUserName}}"] = triggeredByUserName }, attachmentsForThisRule);
                    continue;
                }
                foreach (var (userId, email) in recipients)
                {
                    var tokens = BuildActionTokens(request, userId, baseUrl);
                    tokens["{{ApprovalTimeline}}"] = timelineHtml;
                    tokens["{{LogoUrl}}"] = logoUrl;
                    tokens["{{TriggeredByUserName}}"] = triggeredByUserName;
                    await SendAndLogAsync(rule, request, requestId, mailTemplateId, creatorName, email, ccAddress, tokens, attachmentsForThisRule);
                }
            }
            else
            {
                string? toAddress;
                if (toMode == "SpecificUser")
                {
                    toAddress = root.TryGetProperty("toUserId", out var uid) && uid.TryGetInt32(out var toUserId)
                        ? (await db.AppUsers.FindAsync(toUserId))?.Email
                        : null;
                }
                else if (toMode == "DecisionTriggeredUser")
                {
                    toAddress = triggeredByUser?.Email;
                }
                else
                {
                    toAddress = toMode switch
                    {
                        "Fixed" => root.TryGetProperty("toAddress", out var a) ? a.GetString() : null,
                        "Field" => root.TryGetProperty("toFieldKey", out var fk) ? RequestService.ExtractField(request.DataJson, fk.GetString() ?? "") : null,
                        _ => creator?.Email
                    };
                }
                // Not necessarily an approver (e.g. the creator getting a "Completed" email),
                // so only a plain login-required view link -- no Approve/Reject buttons that
                // would imply they're authorized to decide.
                var extraTokens = new Dictionary<string, string>
                {
                    ["{{RequestUrl}}"] = $"{baseUrl}/Requests/Details/{request.Id}",
                    ["{{ApprovalTimeline}}"] = timelineHtml,
                    ["{{LogoUrl}}"] = logoUrl,
                    ["{{TriggeredByUserName}}"] = triggeredByUserName,
                };
                await SendAndLogAsync(rule, request, requestId, mailTemplateId, creatorName, toAddress, ccAddress, extraTokens, attachmentsForThisRule);
            }
        }

        await db.SaveChangesAsync();
    }

    async Task<int> NextAttemptNoAsync(int ruleId, long requestId) => 1 + await db.PostProcessingExecutions
        .Where(e => e.PostProcessingRuleId == ruleId && e.RequestId == requestId)
        .CountAsync();

    // A rule whose criteria don't match this request never reaches SendAndLogAsync at all --
    // logged here instead so PPF Monitor shows WHY a configured rule didn't fire, the same
    // as it already does for "no recipient address on file", rather than just silence.
    async Task LogSkippedAsync(Core.Models.PostProcessingRule rule, long requestId, string reason)
    {
        var now = DateTime.UtcNow;
        db.PostProcessingExecutions.Add(new Core.Models.PostProcessingExecution
        {
            PostProcessingRuleId = rule.Id,
            RequestId = requestId,
            AttemptNo = await NextAttemptNoAsync(rule.Id, requestId),
            ActionType = rule.ActionType,
            Target = null,
            Status = "Skipped",
            ErrorMessage = reason,
            StartedAt = now,
            FinishedAt = now,
            CreatedAt = now
        });
    }

    // Same shape RequestService.SubmitAsync builds for RoutingService.ResolveAsync at
    // submit time, just read back from the request's current (already-persisted) DataJson
    // instead of a fresh submission payload -- so a PPF rule's criteria can match on the
    // exact same field vocabulary a Routing Rule can, at any event, not just Submit.
    static Dictionary<string, JsonElement> BuildRoutingContext(string? dataJson)
    {
        var context = new Dictionary<string, JsonElement>();
        if (string.IsNullOrWhiteSpace(dataJson)) return context;
        using var doc = JsonDocument.Parse(dataJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return context;
        foreach (var prop in doc.RootElement.EnumerateObject())
            context[prop.Name] = prop.Value.Clone();
        return context;
    }

    async Task SendAndLogAsync(Core.Models.PostProcessingRule rule, Core.Models.Request request, long requestId, int? mailTemplateId, string creatorName, string? toAddress, string? ccAddress, IReadOnlyDictionary<string, string> extraTokens, IReadOnlyList<AttachmentFile>? attachments)
    {
        var startedAt = DateTime.UtcNow;
        var attemptNo = await NextAttemptNoAsync(rule.Id, requestId);

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
                    var (sent, sendError) = await mailSender.SendAsync(toAddress, subject, body, ccAddress, attachments);
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

    // Posts a JSON payload of standard fields + every Data.* field to a configured URL --
    // deliberately no user-authored body template (unlike Mail Templates): there's nothing
    // new to learn to configure one, just a URL and an optional auth header value.
    async Task CallApiAndLogAsync(Core.Models.PostProcessingRule rule, Core.Models.Request request, long requestId, JsonElement root, string approvalTypeName, string creatorName, string eventCode, string triggeredByUserName)
    {
        var startedAt = DateTime.UtcNow;
        var attemptNo = await NextAttemptNoAsync(rule.Id, requestId);
        var apiUrl = root.TryGetProperty("apiUrl", out var u) ? u.GetString() : null;
        var authHeaderValue = root.TryGetProperty("apiAuthHeaderValue", out var ah) ? ah.GetString() : null;

        string status;
        string? error = null;

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            status = "Failed";
            error = "Rule has no API URL configured.";
        }
        else
        {
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["requestNumber"] = request.RequestNumber,
                    ["subject"] = request.Subject,
                    ["status"] = request.Status,
                    ["approvalTypeName"] = approvalTypeName,
                    ["currentLevel"] = request.CurrentLevelNo,
                    ["createdAt"] = request.CreatedAt,
                    ["creatorName"] = creatorName,
                    ["eventCode"] = eventCode,
                    ["triggeredByUserName"] = triggeredByUserName,
                };
                foreach (var (key, value) in MailMergeService.ExtractDataTokens(request.DataJson))
                    payload[key] = value;

                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = JsonContent.Create(payload) };
                if (!string.IsNullOrWhiteSpace(authHeaderValue)) httpRequest.Headers.TryAddWithoutValidation("Authorization", authHeaderValue);

                var response = await client.SendAsync(httpRequest);
                if (response.IsSuccessStatusCode)
                {
                    status = "Sent";
                }
                else
                {
                    status = "Failed";
                    var body = await response.Content.ReadAsStringAsync();
                    error = $"HTTP {(int)response.StatusCode} -- {(body.Length > 500 ? body[..500] : body)}";
                }
            }
            catch (Exception ex)
            {
                status = "Failed";
                error = ex.Message;
            }
        }

        db.PostProcessingExecutions.Add(new Core.Models.PostProcessingExecution
        {
            PostProcessingRuleId = rule.Id,
            RequestId = requestId,
            AttemptNo = attemptNo,
            ActionType = rule.ActionType,
            Target = apiUrl,
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
            var approvers = level.ApproverNames.Count == 0 ? "-" : System.Net.WebUtility.HtmlEncode(string.Join(", ", level.ApproverNames.Select(a => a.DisplayName)));
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
        var sendBackUrl = $"{baseUrl}/EmailAction/SendBackForm?token={Uri.EscapeDataString(token)}";
        return new Dictionary<string, string>
        {
            ["{{RequestUrl}}"] = requestUrl,
            ["{{ApproveUrl}}"] = approveUrl,
            ["{{RejectUrl}}"] = rejectUrl,
            ["{{SendBackUrl}}"] = sendBackUrl,
            ["{{ApproveButton}}"] = $"<a href=\"{approveUrl}\" style=\"display:inline-block;background:#15803d;color:#ffffff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-family:Arial,sans-serif;font-size:14px;\">Approve</a>",
            ["{{RejectButton}}"] = $"<a href=\"{rejectUrl}\" style=\"display:inline-block;background:#b91c1c;color:#ffffff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-family:Arial,sans-serif;font-size:14px;\">Reject</a>",
            ["{{SendBackButton}}"] = $"<a href=\"{sendBackUrl}\" style=\"display:inline-block;background:#b45309;color:#ffffff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:700;font-family:Arial,sans-serif;font-size:14px;\">Send Back</a>",
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
