using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;

namespace JACO.Unified.Web.Services;

// One section of the generated API spec -- Markdown and Word rendering both walk the SAME
// list of these (built once by BuildSections) rather than each hand-writing the content, so
// the two output formats can never drift apart the way two independently-maintained copies
// eventually would.
abstract record DocSection;
sealed record HeadingSection(string Text) : DocSection;
sealed record ParaSection(string Text, bool Italic = false) : DocSection;
sealed record CodeSection(string Text) : DocSection;
sealed record TableSection(string[] Headers, List<string[]> Rows) : DocSection;

// Builds a standalone spec doc (Markdown or Word) for one Approval Type's external API
// contract, for a 3rd party's IT team to build against without needing a live conversation
// with JACO Admin. Generated on demand from the SAME live sources the API Reference screen
// and the API itself read (WorkflowFields via GetFieldSchemaAsync) -- there is no separate
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

    static List<DocSection> BuildSections(ApprovalType type, List<ApprovalFieldSchema> fields, string baseUrl)
    {
        var now = DateTime.UtcNow;
        var exampleRequestNumber = $"{type.Code}-{now:yyyy}-00123";
        var sections = new List<DocSection>();

        sections.Add(new HeadingSection($"JAMS API Specification -- {type.Name}"));
        sections.Add(new ParaSection($"Generated {now:yyyy-MM-dd HH:mm} UTC from the live \"{type.Name}\" ({type.Code}) configuration. " +
            "This document reflects the Criteria Fields as configured right now -- if a field is added, renamed, or removed later, " +
            "re-download this document rather than relying on a cached copy."));

        sections.Add(new HeadingSection("1. Authentication"));
        sections.Add(new ParaSection("Every call requires an X-Api-Key header. Keys are issued by a JAMS administrator under API Access " +
            "(one key per external system/environment) and are shown only once at creation -- store it securely on your side, " +
            "the same way you would a password."));
        sections.Add(new TableSection(["Situation", "HTTP Status", "Body"],
        [
            ["Missing or invalid X-Api-Key", "401 Unauthorized", "{\"error\": \"...\"}"],
            ["External API disabled by the JAMS administrator", "503 Service Unavailable", "{\"error\": \"...\"}"],
            ["More than 60 requests/minute on one key", "429 Too Many Requests", "(rate limiter response)"]
        ]));

        sections.Add(new HeadingSection("2. Base URL"));
        sections.Add(new CodeSection(baseUrl));
        sections.Add(new ParaSection("All endpoint paths below are relative to this base URL."));

        sections.Add(new HeadingSection("3. Endpoints"));
        sections.Add(new TableSection(["Method", "Path", "Description"],
        [
            ["POST", "/api/v1/approvals", "Create and submit a request -- body: approvalTypeCode, subject, externalReference, data{}"],
            ["GET", "/api/v1/approvals/{requestNumber}", "Status, current level, and every field's current value"],
            ["GET", "/api/v1/approvals/{requestNumber}/timeline", "Per-level approvers and decisions so far"],
            ["GET", "/api/v1/approvals/types", "Every active Approval Type's code/name"],
            ["GET", $"/api/v1/approvals/schema/{type.Code}", "This same field list, as JSON"]
        ]));

        sections.Add(new HeadingSection($"4. Field Schema -- {type.Name} ({type.Code})"));
        if (fields.Count == 0)
        {
            sections.Add(new ParaSection("No fields are currently marked \"Include in API\" for this Approval Type.", Italic: true));
        }
        else
        {
            sections.Add(new TableSection(["Order", "Field Key", "Label", "Type", "Required", "Sensitive", "Allowed Values"],
                fields.Select(f => new[]
                {
                    f.DisplayOrder.ToString(),
                    f.FieldKey,
                    f.Label,
                    f.DataType,
                    f.Required ? "Yes" : "--",
                    f.Sensitive ? "Yes" : "--",
                    f.AllowedValues.Count > 0 ? string.Join(", ", f.AllowedValues) : "--"
                }).ToList()));
            sections.Add(new ParaSection("A Sensitive field is hidden from the request's own creator in the browser UI, but is included " +
                "here and on every GET response -- the caller is a trusted system credential, not an end user, so that human-facing " +
                "rule doesn't apply."));
        }

        var exampleData = fields.ToDictionary(f => f.FieldKey, f => (object?)ExampleValue(f));

        sections.Add(new HeadingSection("5. Example -- Create a Request"));
        sections.Add(new ParaSection($"POST {baseUrl}/api/v1/approvals"));
        sections.Add(new ParaSection("Request body:"));
        sections.Add(new CodeSection(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["approvalTypeCode"] = type.Code,
            ["subject"] = "Short summary of the request",
            ["externalReference"] = "Your own reference, e.g. an SAP document number",
            ["data"] = exampleData
        }, JsonOpts)));
        sections.Add(new ParaSection("Response (201 Created):"));
        sections.Add(new CodeSection(JsonSerializer.Serialize(new Dictionary<string, object?>
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
        }, JsonOpts)));

        sections.Add(new HeadingSection("6. Example -- Get Status"));
        sections.Add(new ParaSection($"GET {baseUrl}/api/v1/approvals/{exampleRequestNumber}"));
        sections.Add(new ParaSection("Returns the same shape as the Create response above, reflecting current status/level/data. " +
            "404 Not Found if the request number doesn't exist."));

        sections.Add(new HeadingSection("7. Example -- Get Timeline"));
        sections.Add(new ParaSection($"GET {baseUrl}/api/v1/approvals/{exampleRequestNumber}/timeline"));
        sections.Add(new CodeSection(JsonSerializer.Serialize(new object[]
        {
            new { levelNo = 1, mode = "AnyOne", approvers = new[] { "Jane Approver" }, levelStatus = "Approved",
                decisions = new object[] { new { actorName = "Jane Approver", actionCode = "Approve", comments = (string?)null, atUtc = now.AddHours(-2).ToString("O") } } },
            new { levelNo = 2, mode = "AnyOne", approvers = new[] { "John Manager" }, levelStatus = "Pending",
                decisions = Array.Empty<object>() }
        }, JsonOpts)));

        sections.Add(new HeadingSection("8. Error Responses"));
        sections.Add(new ParaSection("Every error response (besides the rate limiter's 429) has the same shape: {\"error\": \"human-readable message\"}."));
        sections.Add(new TableSection(["Status", "Meaning"],
        [
            ["400 Bad Request", "Missing/invalid approvalTypeCode, unknown/inactive type, or a field failed validation"],
            ["401 Unauthorized", "Missing or invalid X-Api-Key"],
            ["404 Not Found", "No request exists with the given request number"],
            ["429 Too Many Requests", "Rate limit exceeded for this API key (60/minute)"],
            ["503 Service Unavailable", "The external API is currently disabled by a JAMS administrator"],
            ["500 Internal Server Error", "Unexpected server-side error -- retry later; contact JAMS admin if persistent"]
        ]));

        sections.Add(new ParaSection($"Generated live from current configuration on {now:yyyy-MM-dd HH:mm} UTC. " +
            "Not hand-maintained -- re-download after any Criteria Fields change.", Italic: true));

        return sections;
    }

    public static string BuildMarkdown(ApprovalType type, List<ApprovalFieldSchema> fields, string baseUrl)
    {
        var sections = BuildSections(type, fields, baseUrl);
        var sb = new StringBuilder();
        var headingNo = 0;
        foreach (var section in sections)
        {
            switch (section)
            {
                case HeadingSection h:
                    sb.AppendLine(headingNo++ == 0 ? $"# {h.Text}" : $"## {h.Text}");
                    break;
                case ParaSection p:
                    sb.AppendLine(p.Italic ? $"*{p.Text}*" : p.Text);
                    break;
                case CodeSection c:
                    sb.AppendLine("```");
                    sb.AppendLine(c.Text);
                    sb.AppendLine("```");
                    break;
                case TableSection t:
                    sb.AppendLine($"| {string.Join(" | ", t.Headers)} |");
                    sb.AppendLine($"|{string.Concat(t.Headers.Select(_ => "---|"))}");
                    foreach (var row in t.Rows)
                        sb.AppendLine($"| {string.Join(" | ", row)} |");
                    break;
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static byte[] BuildDocx(ApprovalType type, List<ApprovalFieldSchema> fields, string baseUrl)
    {
        var sections = BuildSections(type, fields, baseUrl);

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            var headingNo = 0;
            foreach (var section in sections)
            {
                switch (section)
                {
                    case HeadingSection h:
                        var isTitle = headingNo++ == 0;
                        body.Append(new Paragraph(
                            new ParagraphProperties(new SpacingBetweenLines { Before = "240", After = "160" }),
                            new Run(
                                new RunProperties(new Bold(), new FontSize { Val = isTitle ? "32" : "26" }),
                                new Text(h.Text))));
                        break;
                    case ParaSection p:
                        var runProps = p.Italic ? new RunProperties(new Italic()) : new RunProperties();
                        body.Append(new Paragraph(
                            new ParagraphProperties(new SpacingBetweenLines { After = "160" }),
                            new Run(runProps, new Text(p.Text) { Space = SpaceProcessingModeValues.Preserve })));
                        break;
                    case CodeSection c:
                        var codeRun = new Run(new RunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, new FontSize { Val = "18" }));
                        var lines = c.Text.Replace("\r\n", "\n").Split('\n');
                        for (var i = 0; i < lines.Length; i++)
                        {
                            if (i > 0) codeRun.Append(new Break());
                            codeRun.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
                        }
                        body.Append(new Paragraph(
                            new ParagraphProperties(new SpacingBetweenLines { After = "160" }, new Shading { Val = ShadingPatternValues.Clear, Fill = "F3F3F3" }),
                            codeRun));
                        break;
                    case TableSection t:
                        body.Append(BuildTable(t));
                        body.Append(new Paragraph());
                        break;
                }
            }

            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    static Table BuildTable(TableSection t)
    {
        var table = new Table(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        var headerRow = new TableRow();
        foreach (var h in t.Headers)
            headerRow.Append(new TableCell(
                new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "DDDDDD" }),
                new Paragraph(new Run(new RunProperties(new Bold()), new Text(h)))));
        table.Append(headerRow);

        foreach (var row in t.Rows)
        {
            var tr = new TableRow();
            foreach (var cellText in row)
                tr.Append(new TableCell(new Paragraph(new Run(new Text(cellText) { Space = SpaceProcessingModeValues.Preserve }))));
            table.Append(tr);
        }
        return table;
    }
}
