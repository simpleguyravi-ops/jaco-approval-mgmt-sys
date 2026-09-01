namespace JACO.Unified.Web.Services;

// Minimal RFC4180-ish reader: handles quoted fields (with embedded commas/newlines) and
// "" as an escaped quote. Good enough for admin-maintained upload files (routing rule
// bulk import); not a general-purpose CSV library.
public static class CsvParser
{
    public static List<string[]> Parse(string content)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow() { EndField(); rows.Add(row.ToArray()); row = new List<string>(); }

        while (i < content.Length)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; i++; break;
                case ',': EndField(); i++; break;
                case '\r': i++; break;
                case '\n': EndRow(); i++; break;
                default: field.Append(c); i++; break;
            }
        }
        if (field.Length > 0 || row.Count > 0) EndRow();

        // Strip a UTF-8 BOM that may have leaked into the first cell.
        if (rows.Count > 0 && rows[0].Length > 0 && rows[0][0].Length > 0 && rows[0][0][0] == '﻿')
            rows[0][0] = rows[0][0][1..];

        return rows.Where(r => r.Length > 1 || (r.Length == 1 && r[0].Length > 0)).ToList();
    }
}
