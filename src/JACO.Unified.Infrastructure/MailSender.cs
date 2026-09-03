using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JACO.Unified.Infrastructure;

// Reads live from EmailSettings (admin-editable via /EmailSettings, no redeploy needed)
// on every send; falls back to appsettings-bound EmailOptions only if no row has been
// saved yet (first run before an admin has configured anything through the UI).
public sealed class MailSender(UnifiedDbContext db, IOptions<EmailOptions> options, EmailPasswordProtector protector)
{
    // PpfExecutor points {{LogoUrl}} at "cid:jaco-logo" -- an inline attachment, not an
    // http(s) URL. A URL built from AppBaseUrl (the previous approach) only resolves on
    // whatever machine is running this app; a real recipient opening the email on their
    // own device or phone would just see a broken image, in dev, QA, or production alike.
    // Embedding the file travels the actual bytes with the email instead.
    const string LogoContentId = "jaco-logo";
    static readonly string LogoPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "img", "jaco-logo-color.png");

    public async Task<(bool sent, string? error)> SendAsync(string toAddress, string subject, string bodyHtml, string? ccAddress = null)
    {
        var saved = await db.EmailSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1);
        var enabled = saved?.Enabled ?? options.Value.Enabled;
        var host = saved?.Host ?? options.Value.Host;
        var port = saved?.Port ?? options.Value.Port;
        var useTls = saved?.UseTls ?? options.Value.UseTls;
        var from = saved?.From ?? options.Value.From;
        var username = saved?.Username ?? options.Value.Username;
        var password = saved?.Password is { } encrypted ? protector.Unprotect(encrypted) : options.Value.Password;

        if (!enabled) return (false, "Email disabled in configuration");
        if (string.IsNullOrWhiteSpace(toAddress)) return (false, "No recipient address");

        try
        {
            using var client = new SmtpClient(host, port) { EnableSsl = useTls };
            if (!string.IsNullOrWhiteSpace(username))
                client.Credentials = new NetworkCredential(username, password);

            using var message = new MailMessage { From = new MailAddress(from), Subject = subject };
            AddAddresses(message.To, toAddress);
            if (!string.IsNullOrWhiteSpace(ccAddress)) AddAddresses(message.CC, ccAddress);

            // Body and a manual AlternateView can't both be set -- MailMessage.Body silently
            // creates its own default view, and adding another on top produces two text/html
            // parts in the same message. Only one of these branches runs.
            if (bodyHtml.Contains($"cid:{LogoContentId}", StringComparison.OrdinalIgnoreCase) && File.Exists(LogoPath))
            {
                var htmlView = AlternateView.CreateAlternateViewFromString(bodyHtml, null, "text/html");
                htmlView.LinkedResources.Add(new LinkedResource(LogoPath, "image/png") { ContentId = LogoContentId });
                message.AlternateViews.Add(htmlView);
            }
            else
            {
                message.Body = bodyHtml;
                message.IsBodyHtml = true;
            }

            await client.SendMailAsync(message);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, DescribeError(ex));
        }
    }

    // A rule's Fixed/CC address field accepts more than one recipient, comma- or
    // semicolon-separated (matching how most people already type a recipient list).
    static void AddAddresses(MailAddressCollection collection, string addresses)
    {
        foreach (var part in addresses.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            collection.Add(part);
    }

    // SmtpException.Message is frequently just the generic "Failure sending mail." --
    // the actual cause (DNS failure, connection refused, TLS/auth rejection) sits in
    // InnerException, sometimes nested more than one level deep (e.g. a SocketException
    // under an IOException under the SmtpException). Walk the chain so PPF Monitor's
    // Detail column shows something an admin can actually act on.
    static string DescribeError(Exception ex)
    {
        var messages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !messages.Contains(e.Message))
                messages.Add(e.Message);
        return string.Join(" -- ", messages);
    }
}
