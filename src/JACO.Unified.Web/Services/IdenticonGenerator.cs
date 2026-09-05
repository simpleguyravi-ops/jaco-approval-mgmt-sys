using System.Security.Cryptography;
using System.Text;

namespace JACO.Unified.Web.Services;

// A solid-colour circle with the person's initials -- no stored image, no upload, the same
// seed always renders the same avatar everywhere it appears. The colour is a hash of the
// seed so two people don't collide visually; the initials are read straight off the seed
// itself, so callers keep passing whatever display string they already show next to the
// avatar (a name or a username) and the two stay in sync. Returned as a data: URI so any
// call site can just drop it into a plain `background-image` (server-rendered markup) or a
// JSON payload a script reads (client-rendered chips).
public static class IdenticonGenerator
{
    public static string DataUri(string seed, int size = 40)
    {
        var svg = Svg(seed, size);
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    public static string Svg(string seed, int size = 40)
    {
        seed ??= "";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        var hue = hash[0] / 255.0 * 360.0;
        var color = $"hsl({hue:F0},48%,42%)";

        var initials = Initials(seed);
        var fontSize = size * 0.42;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {size} {size}'>");
        sb.Append($"<circle cx='{size / 2.0:F2}' cy='{size / 2.0:F2}' r='{size / 2.0:F2}' fill='{color}'/>");
        sb.Append($"<text x='50%' y='51%' dy='0.35em' text-anchor='middle' font-family='Segoe UI, Arial, sans-serif' font-size='{fontSize:F1}' font-weight='700' fill='#fff'>{initials}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    static string Initials(string seed)
    {
        var words = seed.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
            return char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[^1][0]);
        if (words.Length == 1)
            return words[0].Length >= 2
                ? char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[0][1])
                : char.ToUpperInvariant(words[0][0]).ToString();
        return "?";
    }
}
