using System.Text;

namespace JACO.Unified.Web.Services;

// Shared by every admin export action (Cockpit's routing/audit logs, All Approvals).
// Plain CSV via StringBuilder -- no Excel library referenced in the solution, and a CSV
// with a UTF-8 BOM opens correctly formatted in Excel, which covers the actual ask.
public static class CsvHelper
{
    public static string ToCsv<T>(List<T> rows, string[] header, Func<T, string[]> selector)
    {
        // A value starting with =, +, -, or @ is interpreted as a formula by Excel/Sheets
        // when the CSV is opened -- since every column here can carry user-submitted text
        // (Subject, Comments, submitted field values), a leading apostrophe neutralizes that
        // without changing what a human reader sees.
        string Neutralize(string v) => v.Length > 0 && (v[0] is '=' or '+' or '-' or '@') ? "'" + v : v;
        string Escape(string v) => (v = Neutralize(v)).Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", selector(row).Select(Escape)));
        return sb.ToString();
    }

    public static byte[] ToCsvBytes<T>(List<T> rows, string[] header, Func<T, string[]> selector)
    {
        var csv = ToCsv(rows, header, selector);
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
    }
}
