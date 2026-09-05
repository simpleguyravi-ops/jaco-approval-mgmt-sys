using System.Security.Cryptography;
using System.Text;

namespace JACO.Unified.Web.Services;

// A GitHub-style identicon: a symmetric 5x5 pixel grid plus a hue, both derived from a hash
// of the seed (a user's UserName -- stable and unique, unlike DisplayName) -- no stored
// image, no upload, the same seed always renders the same identicon everywhere it appears.
// Returned as a data: URI so any call site can just drop it into a plain `background-image`
// (server-rendered markup) or a JSON payload a script reads (client-rendered chips) --
// nothing has to re-implement the hashing/drawing logic in JS to stay in sync.
public static class IdenticonGenerator
{
    public static string DataUri(string seed, int size = 40)
    {
        var svg = Svg(seed, size);
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    public static string Svg(string seed, int size = 40)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed ?? ""));
        var hue = hash[0] / 255.0 * 360.0;
        var color = $"hsl({hue:F0},62%,52%)";
        var bg = $"hsl({hue:F0},55%,94%)";

        var cell = size / 5.0;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {size} {size}'>");
        sb.Append($"<rect width='{size}' height='{size}' fill='{bg}'/>");

        // Only columns 0-2 are hashed; columns 3-4 mirror 1-0 so the pattern reads as one
        // symmetric mark rather than static noise. hash[0] is spent on the hue above, so
        // bit-plotting starts at hash[1].
        for (var row = 0; row < 5; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var bitIndex = row * 3 + col;
                var b = hash[1 + (bitIndex / 8) % (hash.Length - 1)];
                var on = ((b >> (bitIndex % 8)) & 1) == 1;
                if (!on) continue;

                var y = row * cell;
                sb.Append($"<rect x='{(col * cell):F2}' y='{y:F2}' width='{cell:F2}' height='{cell:F2}' fill='{color}'/>");
                if (col != 2) // col 2 is the center column -- mirroring it onto itself would double-draw
                    sb.Append($"<rect x='{((4 - col) * cell):F2}' y='{y:F2}' width='{cell:F2}' height='{cell:F2}' fill='{color}'/>");
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
