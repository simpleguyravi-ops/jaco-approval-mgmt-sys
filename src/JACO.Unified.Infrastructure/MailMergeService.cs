using System.Text;
using System.Text.Json;
using JACO.Unified.Core.Models;

namespace JACO.Unified.Infrastructure;

public static class MailMergeService
{
    // extraHtmlTokens: pre-built, already-safe HTML/URL values (action links, button markup)
    // merged in AFTER the general HtmlEncode pass and only into the BODY, never the subject --
    // encoding them like ordinary data would mangle a URL's "&"/"=" or literally show a
    // <a href> tag as text instead of rendering it.
    public static (string Subject, string Body) RenderSingle(MailTemplate template, Request request, string creatorName, IReadOnlyDictionary<string, string>? extraHtmlTokens = null)
    {
        var tokens = new Dictionary<string, string>
        {
            ["{{RequestNumber}}"] = request.RequestNumber,
            ["{{Subject}}"] = request.Subject ?? "",
            ["{{Status}}"] = request.Status,
            ["{{CurrentLevel}}"] = request.CurrentLevelNo?.ToString() ?? "-",
            ["{{CreatorName}}"] = creatorName,
            ["{{CreatedAt}}"] = request.CreatedAt.ToString("dd MMM yyyy HH:mm"),
        };
        foreach (var (key, value) in ExtractDataTokens(request.DataJson))
            tokens[$"{{{{Data.{key}}}}}"] = value;

        var htmlTokens = tokens.ToDictionary(t => t.Key, t => Html(t.Value));
        if (extraHtmlTokens is not null)
            foreach (var (key, value) in extraHtmlTokens) htmlTokens[key] = value;

        return (Replace(template.Subject, tokens), Replace(template.BodyHtml, htmlTokens));
    }

    public static (string Subject, string Body) RenderTable(MailTemplate template, string recipientName, IReadOnlyList<Request> requests)
    {
        var rows = new StringBuilder();
        foreach (var r in requests)
        {
            rows.Append("<tr><td>").Append(Html(r.RequestNumber)).Append("</td><td>")
                .Append(Html(r.Subject ?? "")).Append("</td><td>")
                .Append(r.CurrentLevelNo?.ToString() ?? "-").Append("</td><td>")
                .Append(r.CreatedAt.ToString("dd MMM yyyy HH:mm")).Append("</td></tr>");
        }
        // Subject is plain text (never HTML-rendered) so the raw name is fine there, but the
        // BODY is later shown via @Html.Raw both in the email itself and on the admin-facing
        // Digest Run Detail page -- recipientName is a user's own display name (can originate
        // from a self-service profile field, not just an admin), so unlike TableRows (already
        // per-cell encoded above) it must be encoded before landing in the body.
        var subjectTokens = new Dictionary<string, string>
        {
            ["{{RecipientName}}"] = recipientName,
            ["{{PendingCount}}"] = requests.Count.ToString(),
        };
        var bodyTokens = new Dictionary<string, string>
        {
            ["{{RecipientName}}"] = Html(recipientName),
            ["{{PendingCount}}"] = requests.Count.ToString(),
            ["{{TableRows}}"] = rows.ToString(),
        };
        return (Replace(template.Subject, subjectTokens), Replace(template.BodyHtml, bodyTokens));
    }

    // Flat DataJson (no routingContext/decisionData nesting -- every WorkflowField value
    // is available for both routing criteria AND display/merge, since the same catalog now
    // drives both).
    public static IReadOnlyDictionary<string, string> ExtractDataTokens(string? dataJson)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(dataJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    JsonValueKind.Object or JsonValueKind.Array => "",
                    _ => prop.Value.ToString()
                };
            }
        }
        catch (JsonException) { /* malformed/legacy DataJson -- render without Data.* tokens */ }
        return result;
    }

    static string Replace(string text, Dictionary<string, string> tokens)
    {
        foreach (var (key, value) in tokens) text = text.Replace(key, value);
        return text;
    }

    static string Html(string s) => System.Net.WebUtility.HtmlEncode(s);
}
