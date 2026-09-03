using System.Text;
using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;

namespace JACO.Unified.Web.Services;

// Builds a standalone Markdown spec doc for one Approval Type's external API contract, for
// a 3rd party's IT team to build against without needing a live conversation with JACO
// Admin. Generated on demand from the SAME live sources the API Reference screen and the
// API itself read (WorkflowFields via GetFieldSchemaAsync) -- there is no separate
// hand-maintained copy of this content to fall out of sync.
public static class ApiSpecGenerator
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static object ExampleValue(ApprovalFieldSchema f)
    {
        if (f.AllowedValues.Count > 0) return f.AllowedValues[0];
        return f.DataType switch
        {
            FieldDataType.Number => 1,
            FieldDataType.Currency => 100.00,
            FieldDataType.Date => DateTime.UtcNow.ToString("yyyy-MM-dd"),
            FieldDataType.TextArea => $"{f.Label} text",
            _ => f.Label
        };
    }

    public static string BuildMarkdown(ApprovalType type, List<ApprovalFieldSchema> fields, string baseUrl)
    {
        var now = DateTime.UtcNow;
        var exampleRequestNumber = $"{type.Code}-{now:yyyy}-00123";
        var sb = new StringBuilder();

        sb.AppendLine($"# JAMS API Specification -- {type.Name}");
        sb.AppendLine();
        sb.AppendLine($"Generated {now:yyyy-MM-dd HH:mm} UTC from the live \"{type.Name}\" ({type.Code}) configuration. " +
            "This document reflects the Criteria Fields as configured *right now* -- if a field is added, renamed, or removed later, " +
            "re-download this document rather than relying on a cached copy.");
        sb.AppendLine();

        sb.AppendLine("## 1. Authentication");
        sb.AppendLine();
        sb.AppendLine("Every call requires an `X-Api-Key` header. Keys are issued by a JAMS administrator under **API Access** " +
            "(one key per external system/environment) and are shown only once at creation -- store it securely on your side, " +
            "the same way you would a password.");
        sb.AppendLine();
        sb.AppendLine("| Situation | HTTP Status | Body |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| Missing or invalid `X-Api-Key` | `401 Unauthorized` | `{\"error\": \"...\"}` |");
        sb.AppendLine("| External API disabled by the JAMS administrator | `503 Service Unavailable` | `{\"error\": \"...\"}` |");
        sb.AppendLine("| More than 60 requests/minute on one key | `429 Too Many Requests` | (rate limiter response) |");
        sb.AppendLine();

        sb.AppendLine("## 2. Base URL");
        sb.AppendLine();
        sb.AppendLine($"```\n{baseUrl}\n```");
        sb.AppendLine();
        sb.AppendLine("All endpoint paths below are relative to this base URL.");
        sb.AppendLine();

        sb.AppendLine("## 3. Endpoints");
        sb.AppendLine();
        sb.AppendLine("| Method | Path | Description |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| `POST` | `/api/v1/approvals` | Create and submit a request -- body: `approvalTypeCode`, `subject`, `externalReference`, `data{}` |");
        sb.AppendLine("| `GET` | `/api/v1/approvals/{requestNumber}` | Status, current level, and every field's current value |");
        sb.AppendLine("| `GET` | `/api/v1/approvals/{requestNumber}/timeline` | Per-level approvers and decisions so far |");
        sb.AppendLine("| `GET` | `/api/v1/approvals/types` | Every active Approval Type's code/name |");
        sb.AppendLine($"| `GET` | `/api/v1/approvals/schema/{type.Code}` | This same field list, as JSON |");
        sb.AppendLine();

        sb.AppendLine($"## 4. Field Schema -- {type.Name} (`{type.Code}`)");
        sb.AppendLine();
        if (fields.Count == 0)
        {
            sb.AppendLine("*No fields are currently marked \"Include in API\" for this Approval Type.*");
        }
        else
        {
            sb.AppendLine("| Order | Field Key | Label | Type | Required | Sensitive | Allowed Values |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var f in fields)
            {
                var allowed = f.AllowedValues.Count > 0 ? string.Join(", ", f.AllowedValues) : "--";
                sb.AppendLine($"| {f.DisplayOrder} | `{f.FieldKey}` | {f.Label} | {f.DataType} | {(f.Required ? "Yes" : "--")} | {(f.Sensitive ? "Yes" : "--")} | {allowed} |");
            }
            sb.AppendLine();
            sb.AppendLine("A **Sensitive** field is hidden from the request's own creator in the browser UI, but is included here " +
                "and on every `GET` response -- the caller is a trusted system credential, not an end user, so that human-facing " +
                "rule doesn't apply.");
        }
        sb.AppendLine();

        var exampleData = fields.ToDictionary(f => f.FieldKey, f => (object?)ExampleValue(f));

        sb.AppendLine("## 5. Example -- Create a Request");
        sb.AppendLine();
        sb.AppendLine($"`POST {baseUrl}/api/v1/approvals`");
        sb.AppendLine();
        sb.AppendLine("Request body:");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["approvalTypeCode"] = type.Code,
            ["subject"] = "Short summary of the request",
            ["externalReference"] = "Your own reference, e.g. an SAP document number",
            ["data"] = exampleData
        }, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Response (`201 Created`):");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["requestNumber"] = exampleRequestNumber,
            ["approvalType"] = type.Name,
            ["status"] = "Pending Approval",
            ["currentLevel"] = 1,
            ["subject"] = "Short summary of the request",
            ["externalReference"] = "Your own reference, e.g. an SAP document number",
            ["createdBy"] = "your.integration.user",
            ["createdAtUtc"] = now.ToString("O"),
            ["updatedAtUtc"] = now.ToString("O"),
            ["data"] = exampleData.ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.ToString())
        }, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## 6. Example -- Get Status");
        sb.AppendLine();
        sb.AppendLine($"`GET {baseUrl}/api/v1/approvals/{exampleRequestNumber}`");
        sb.AppendLine();
        sb.AppendLine("Returns the same shape as the Create response above, reflecting current status/level/data. " +
            "`404 Not Found` if the request number doesn't exist.");
        sb.AppendLine();

        sb.AppendLine("## 7. Example -- Get Timeline");
        sb.AppendLine();
        sb.AppendLine($"`GET {baseUrl}/api/v1/approvals/{exampleRequestNumber}/timeline`");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(new object[]
        {
            new { levelNo = 1, mode = "AnyOne", approvers = new[] { "Jane Approver" }, levelStatus = "Approved",
                decisions = new object[] { new { actorName = "Jane Approver", actionCode = "Approve", comments = (string?)null, atUtc = now.AddHours(-2).ToString("O") } } },
            new { levelNo = 2, mode = "AnyOne", approvers = new[] { "John Manager" }, levelStatus = "Pending",
                decisions = Array.Empty<object>() }
        }, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## 8. Error Responses");
        sb.AppendLine();
        sb.AppendLine("Every error response (besides the rate limiter's `429`) has the same shape: `{\"error\": \"human-readable message\"}`.");
        sb.AppendLine();
        sb.AppendLine("| Status | Meaning |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `400 Bad Request` | Missing/invalid `approvalTypeCode`, unknown/inactive type, or a field failed validation |");
        sb.AppendLine("| `401 Unauthorized` | Missing or invalid `X-Api-Key` |");
        sb.AppendLine("| `404 Not Found` | No request exists with the given request number |");
        sb.AppendLine("| `429 Too Many Requests` | Rate limit exceeded for this API key (60/minute) |");
        sb.AppendLine("| `503 Service Unavailable` | The external API is currently disabled by a JAMS administrator |");
        sb.AppendLine("| `500 Internal Server Error` | Unexpected server-side error -- retry later; contact JAMS admin if persistent |");
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine($"*Generated live from current configuration on {now:yyyy-MM-dd HH:mm} UTC. Not hand-maintained -- re-download after any Criteria Fields change.*");

        return sb.ToString();
    }
}
