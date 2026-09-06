using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JACO.Unified.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JACO.Unified.Infrastructure;

// Ad hoc "share this one request with someone outside the approval chain" -- unlike PPF
// (admin-configured rules firing on an event) this is a deliberate, occasional, single
// action a creator/admin triggers by hand with a freshly-typed address, so it runs inline
// synchronously rather than through the NotificationQueue -- the same reasoning
// EmailSettingsController's "Send Test" and the Digest/Reminder "Run Now" buttons already
// use: a manual send the clicker wants immediate feedback on, not a high-volume automatic
// one that must never block the caller.
public sealed class RequestDetailsMailer(UnifiedDbContext db, MailSender mailSender, RequestAttachmentStorage attachmentStorage, IConfiguration configuration)
{
    static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public async Task<(bool ok, string message)> SendAsync(long requestId, string toAddress, int senderUserId, bool isAdmin)
    {
        toAddress = (toAddress ?? "").Trim();
        if (!EmailPattern.IsMatch(toAddress)) return (false, "Enter a valid email address.");

        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return (false, "Request not found.");
        if (!isAdmin && request.CreatorUserId != senderUserId) return (false, "Only the creator can share this request's details.");

        var type = await db.ApprovalTypes.FindAsync(request.ApprovalTypeId);
        var creator = await db.AppUsers.FindAsync(request.CreatorUserId);
        var sender = await db.AppUsers.FindAsync(senderUserId);

        // Sensitive fields are never included here -- unlike the Details page (which hides
        // them from the creator specifically, but still shows them to an approver/admin),
        // the recipient of this email is an arbitrary address typed in at send time, not a
        // known system role, so there's no reliable basis for deciding they should see them.
        var fields = await db.WorkflowFields
            .Where(f => f.Active && f.IsVisible && !f.IsSensitive && (f.ApprovalTypeId == request.ApprovalTypeId || (f.ApprovalTypeId == null && f.TaskTypeId == null)))
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync();

        var attachmentRows = await db.RequestAttachments.Where(a => a.RequestId == requestId).ToListAsync();
        var attachmentFiles = attachmentRows
            .Select(a => new AttachmentFile(a.OriginalFileName, attachmentStorage.GetPath(requestId, a.StoredFileName), a.ContentType))
            .ToList();

        var baseUrl = (configuration["AppBaseUrl"] ?? "http://localhost:5004").TrimEnd('/');
        var subject = $"{request.RequestNumber} -- {(string.IsNullOrWhiteSpace(request.Subject) ? type?.Name ?? "Request" : request.Subject)} (shared by {sender?.DisplayName ?? "a JAMS user"})";
        var body = BuildBodyHtml(request, type, creator, fields, attachmentRows, baseUrl);

        var (sent, error) = await mailSender.SendAsync(toAddress, subject, body, attachments: attachmentFiles);

        db.AuditLogs.Add(new AuditLog
        {
            RequestId = requestId,
            UserId = senderUserId,
            ActionCode = "DetailsEmailed",
            DetailsJson = JsonSerializer.Serialize(new
            {
                to = toAddress,
                attachmentCount = attachmentFiles.Count,
                sent,
                error
            }),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return sent ? (true, $"Details sent to {toAddress}.") : (false, $"Not sent: {error}");
    }

    static string BuildBodyHtml(Request request, ApprovalType? type, AppUser? creator, List<WorkflowField> fields, List<RequestAttachment> attachments, string baseUrl)
    {
        var sb = new StringBuilder();
        sb.Append("<p>").Append(WebUtility.HtmlEncode(request.RequestNumber));
        if (!string.IsNullOrWhiteSpace(request.Subject)) sb.Append(" &mdash; ").Append(WebUtility.HtmlEncode(request.Subject));
        sb.Append("</p>");

        sb.Append("<table role=\"presentation\" style=\"border-collapse:collapse;font-family:Arial,sans-serif;font-size:13px;margin-bottom:14px;\">");
        AppendRow(sb, "Approval Type", type?.Name ?? "-");
        AppendRow(sb, "Status", request.Status);
        AppendRow(sb, "Created By", creator?.DisplayName ?? request.CreatorUserName);
        AppendRow(sb, "Created", request.CreatedAt.ToString("dd MMM yyyy HH:mm"));
        sb.Append("</table>");

        if (fields.Count > 0)
        {
            sb.Append("<table role=\"presentation\" style=\"border-collapse:collapse;font-family:Arial,sans-serif;font-size:13px;\">");
            foreach (var f in fields)
                AppendRow(sb, f.FieldLabel, RequestService.ExtractField(request.DataJson, f.FieldKey) is { } v && v.Length > 0 ? v : "-");
            sb.Append("</table>");
        }

        if (attachments.Count > 0)
        {
            sb.Append("<p style=\"font-family:Arial,sans-serif;font-size:13px;margin-top:14px;\"><b>Attachments:</b> ")
              .Append(WebUtility.HtmlEncode(string.Join(", ", attachments.Select(a => a.OriginalFileName))))
              .Append(" (attached to this email)</p>");
        }

        sb.Append("<p style=\"margin-top:16px;\"><a href=\"").Append(baseUrl).Append("/Requests/Details/").Append(request.Id)
          .Append("\" style=\"display:inline-block;background:#f2600c;color:#ffffff;padding:10px 24px;border-radius:6px;text-decoration:none;font-weight:700;font-family:Arial,sans-serif;font-size:13px;\">Open in JAMS</a></p>")
          .Append("<p style=\"color:#6b7280;font-size:12px;margin-top:16px;font-family:Arial,sans-serif;\">Shared from JAMS. The link above requires a JAMS login to view.</p>");

        return sb.ToString();
    }

    static void AppendRow(StringBuilder sb, string label, string value) =>
        sb.Append("<tr><td style=\"padding:3px 12px 3px 0;color:#6b7280;vertical-align:top;\">").Append(WebUtility.HtmlEncode(label))
          .Append("</td><td style=\"padding:3px 0;\">").Append(WebUtility.HtmlEncode(value)).Append("</td></tr>");
}
