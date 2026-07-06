using System.Text;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Builds a development/testing placeholder PromptPay QR (an SVG data URI) so agents and local
/// testers can exercise the in-chat QR card without a live Omise charge. This is NOT a scannable
/// code — it is clearly labelled as a dev placeholder and is only used on dev/test prototype paths.
/// </summary>
internal static class DevPromptPayQr
{
    public static string Build(decimal amount, string currency)
    {
        const int modules = 21;
        const int scale = 9;
        var size = modules * scale;
        var normalizedCurrency = string.IsNullOrWhiteSpace(currency) ? "THB" : currency;
        var seed = unchecked((int)(amount * 100m) * 2654435761 ^ StringComparer.Ordinal.GetHashCode(normalizedCurrency));

        static bool IsFinderRing(int localX, int localY) =>
            localX == 0 || localX == 6 || localY == 0 || localY == 6 ||
            (localX >= 2 && localX <= 4 && localY >= 2 && localY <= 4);

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{size}' height='{size + 32}' viewBox='0 0 {size} {size + 32}'>");
        sb.Append($"<rect width='{size}' height='{size + 32}' fill='#ffffff'/>");
        for (var y = 0; y < modules; y++)
        {
            for (var x = 0; x < modules; x++)
            {
                bool on;
                var inTopLeft = x < 7 && y < 7;
                var inTopRight = x >= modules - 7 && y < 7;
                var inBottomLeft = x < 7 && y >= modules - 7;
                if (inTopLeft || inTopRight || inBottomLeft)
                {
                    var lx = x < 7 ? x : x - (modules - 7);
                    var ly = y < 7 ? y : y - (modules - 7);
                    on = IsFinderRing(lx, ly);
                }
                else
                {
                    seed = unchecked(seed * 1103515245 + 12345);
                    on = ((seed >> 16) & 1) == 1;
                }

                if (on)
                {
                    sb.Append($"<rect x='{x * scale}' y='{y * scale}' width='{scale}' height='{scale}' fill='#111111'/>");
                }
            }
        }
        sb.Append($"<text x='{size / 2}' y='{size + 21}' font-family='sans-serif' font-size='12' text-anchor='middle' fill='#111111'>DEV PromptPay · {amount:0.##} {normalizedCurrency}</text>");
        sb.Append("</svg>");

        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
