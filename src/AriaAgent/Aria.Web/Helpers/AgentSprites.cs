using System.Text;

namespace Aria.Web.Helpers;

// Warhammer 40K pixel-art avatar sprites — lookup/rendering logic.
// Sprite pixel data lives in AgentSprites.Sprites.cs.
public static partial class AgentSprites
{
    // Portrait PNG library: archetype-key → number of numbered files in wwwroot/avatars/
    private static readonly Dictionary<string, int> PortraitCounts = new()
    {
        ["space-marine"]     = 10,
        ["chaos-marine"]     = 10,
        ["tech-priest"]      = 10,
        ["inquisitor"]       = 10,
        ["commissar"]        = 10,
        ["guardsman"]        = 10,
        ["sister"]           = 10,
        ["skitarii"]         = 10,
        ["navigator"]        = 10,
        ["ork-warboss"]      = 12,
        ["farseer"]          = 10,
        ["necron-overlord"]  = 10,
        ["chaos-sorcerer"]   = 10,
        ["votann-kin"]       = 10,
    };

    // Returns true if key refers to a generated PNG portrait (e.g. "ork-warboss-7")
    public static bool IsPortraitKey(string? key)
    {
        if (key == null) return false;
        var i = key.LastIndexOf('-');
        return i > 0 && int.TryParse(key.AsSpan(i + 1), out _) && PortraitCounts.ContainsKey(key[..i]);
    }

    // Picks a random portrait key. When archetypeName matches a known race the portrait
    // comes from that race; otherwise a completely random race is chosen.
    public static string PickSpriteKey(string? archetypeName = null)
    {
        var baseKey = string.IsNullOrEmpty(archetypeName) ? null : GetBaseKey(archetypeName);
        if (baseKey == null || !PortraitCounts.ContainsKey(baseKey))
            baseKey = PortraitCounts.Keys.ElementAt(Random.Shared.Next(PortraitCounts.Count));
        return $"{baseKey}-{Random.Shared.Next(1, PortraitCounts[baseKey] + 1)}";
    }

    // Returns the portrait library key for a given archetype name, or null if no match.
    private static string? GetBaseKey(string archetypeName)
    {
        var n = archetypeName.ToLowerInvariant();
        if (n.Contains("ork") || n.Contains("warboss") || n.Contains("mekboy") || n.Contains("greenskin"))
            return "ork-warboss";
        if (n.Contains("tzeentch") || n.Contains("sorcerer"))
            return "chaos-sorcerer";
        if (n.Contains("chaos") || n.Contains("heretic") || n.Contains("renegade") || n.Contains("traitor"))
            return "chaos-marine";
        if (n.Contains("tech") || n.Contains("magos") || n.Contains("mechanicus") || n.Contains("enginseer"))
            return "tech-priest";
        if (n.Contains("inquisit"))
            return "inquisitor";
        if (n.Contains("commissar"))
            return "commissar";
        if (n.Contains("guard") || n.Contains("soldier") || n.Contains("trooper") || n.Contains("militarum"))
            return "guardsman";
        if (n.Contains("sister") || n.Contains("sororitas") || n.Contains("celestian") || n.Contains("seraphim"))
            return "sister";
        if (n.Contains("skitarii") || n.Contains("ranger") || n.Contains("vanguard") || n.Contains("sicarian"))
            return "skitarii";
        if (n.Contains("navigator") || n.Contains("psyker") || n.Contains("warp") || n.Contains("sanctioned"))
            return "navigator";
        if (n.Contains("marine") || n.Contains("astartes") || n.Contains("chapter") || n.Contains("space"))
            return "space-marine";
        if (n.Contains("farseer") || n.Contains("eldar") || n.Contains("aeldari") || n.Contains("warlock"))
            return "farseer";
        if (n.Contains("necron") || n.Contains("overlord") || n.Contains("cryptek") || n.Contains("phaeron"))
            return "necron-overlord";
        if (n.Contains("votann") || n.Contains("squat") || n.Contains("kinherd") || n.Contains("grimnyr"))
            return "votann-kin";
        return null;
    }

    // Unified render — detects portrait PNG keys vs hand-drawn sprite keys automatically.
    // displaySize = CSS height in px; portrait width is 2/3 of height (2:3 ratio).
    public static string RenderAuto(string? key, string accentHex, int displaySize = 32) =>
        IsPortraitKey(key)
            ? RenderPortraitSvg(key!, accentHex, displaySize * 2 / 3, displaySize)
            : RenderSvg(key, accentHex, displaySize);

    // Renders the Soul (human user) avatar: circular frame with anonymous person silhouette.
    public static string RenderSoulAvatar(int displaySize = 32)
    {
        var s = displaySize;
        return $"<svg width=\"{s}\" height=\"{s}\" viewBox=\"0 0 32 32\" xmlns=\"http://www.w3.org/2000/svg\" " +
               "style=\"display:inline-block;vertical-align:middle;flex-shrink:0\">" +
               "<circle cx=\"16\" cy=\"16\" r=\"15\" fill=\"#111\" stroke=\"#444\" stroke-width=\"1\"/>" +
               // head
               "<circle cx=\"16\" cy=\"12\" r=\"5\" fill=\"#666\"/>" +
               // shoulders
               "<path d=\"M4 30 Q4 21 16 21 Q28 21 28 30\" fill=\"#666\"/>" +
               "</svg>";
    }

    // Renders a generated PNG portrait embedded in an SVG vox-comm frame.
    // Uses 2:3 coordinate space (0 0 2 3) with 0.2-unit padding for the frame.
    private static string RenderPortraitSvg(string key, string accentHex, int w, int h)
    {
        var (dark, _, bright, dim) = ComputeColors(accentHex);
        var sb = new StringBuilder(800);
        sb.Append($"<svg width=\"{w}\" height=\"{h}\" viewBox=\"-0.2 -0.2 2.4 3.4\" " +
                  "xmlns=\"http://www.w3.org/2000/svg\" " +
                  "style=\"display:inline-block;vertical-align:middle;flex-shrink:0\">");
        sb.Append($"<rect x=\"0\" y=\"0\" width=\"2\" height=\"3\" fill=\"{dark}\"/>");
        sb.Append($"<image href=\"/avatars/{key}.png\" x=\"0\" y=\"0\" width=\"2\" height=\"3\" preserveAspectRatio=\"xMidYMid slice\"/>");
        sb.Append($"<rect x=\"0\" y=\"1.5\" width=\"2\" height=\"0.06\" fill=\"{bright}\" opacity=\"0.07\"/>");
        sb.Append($"<rect x=\"-0.05\" y=\"-0.05\" width=\"2.1\" height=\"3.1\" fill=\"none\" stroke=\"{dim}\" stroke-width=\"0.05\"/>");
        // top-left
        sb.Append($"<rect x=\"-0.18\" y=\"-0.18\" width=\"0.50\" height=\"0.12\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"-0.18\" y=\"-0.18\" width=\"0.12\" height=\"0.50\" fill=\"{bright}\"/>");
        // top-right
        sb.Append($"<rect x=\"1.68\" y=\"-0.18\" width=\"0.50\" height=\"0.12\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"2.06\" y=\"-0.18\" width=\"0.12\" height=\"0.50\" fill=\"{bright}\"/>");
        // bottom-left
        sb.Append($"<rect x=\"-0.18\" y=\"3.06\" width=\"0.50\" height=\"0.12\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"-0.18\" y=\"2.68\" width=\"0.12\" height=\"0.50\" fill=\"{bright}\"/>");
        // bottom-right
        sb.Append($"<rect x=\"1.68\" y=\"3.06\" width=\"0.50\" height=\"0.12\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"2.06\" y=\"2.68\" width=\"0.12\" height=\"0.50\" fill=\"{bright}\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    // Returns a framed SVG portrait — sprite + Warhammer vox-comm overlay frame.
    // ViewBox is -1,-1 18,18 (1 unit padding around the 16×16 sprite) so corner
    // brackets fit without clipping. displaySize is the CSS pixel size.
    public static string RenderSvg(string? spriteKey, string accentHex, int displaySize = 32)
    {
        var key    = spriteKey != null && Data.ContainsKey(spriteKey) ? spriteKey : Data.Keys.First();
        var sprite = Data[key];
        var (dark, mid, bright, dim) = ComputeColors(accentHex);

        var sb = new StringBuilder(3000);
        sb.Append($"<svg width=\"{displaySize}\" height=\"{displaySize}\" viewBox=\"-1 -1 18 18\" " +
                  "xmlns=\"http://www.w3.org/2000/svg\" shape-rendering=\"crispEdges\" " +
                  "style=\"display:inline-block;vertical-align:middle;image-rendering:pixelated\">");

        // Dark terminal background (transparent sprite pixels show this, not the page bg)
        sb.Append($"<rect x=\"0\" y=\"0\" width=\"16\" height=\"16\" fill=\"{dark}\"/>");

        // Sprite pixels
        for (int i = 0; i < 256 && i < sprite.Length; i++)
        {
            var v = sprite[i] - '0';
            if (v == 0) continue;
            int col = i % 16, row = i / 16;
            var fill = v == 1 ? dark : v == 2 ? mid : bright;
            sb.Append($"<rect x=\"{col}\" y=\"{row}\" width=\"1\" height=\"1\" fill=\"{fill}\"/>");
        }

        // Subtle horizontal scan line across mid-face
        sb.Append($"<rect x=\"0\" y=\"8\" width=\"16\" height=\"0.4\" fill=\"{bright}\" opacity=\"0.10\"/>");

        // Outer thin border
        sb.Append($"<rect x=\"-0.5\" y=\"-0.5\" width=\"17\" height=\"17\" fill=\"none\" stroke=\"{dim}\" stroke-width=\"0.4\"/>");

        // Corner L-brackets (1 unit thick, 4 units long) — vox targeting frame
        // top-left
        sb.Append($"<rect x=\"-1\" y=\"-1\" width=\"4\" height=\"1\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"-1\" y=\"-1\" width=\"1\" height=\"4\" fill=\"{bright}\"/>");
        // top-right
        sb.Append($"<rect x=\"13\" y=\"-1\" width=\"4\" height=\"1\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"16\"  y=\"-1\" width=\"1\" height=\"4\" fill=\"{bright}\"/>");
        // bottom-left
        sb.Append($"<rect x=\"-1\" y=\"16\" width=\"4\" height=\"1\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"-1\" y=\"13\" width=\"1\" height=\"4\" fill=\"{bright}\"/>");
        // bottom-right
        sb.Append($"<rect x=\"13\" y=\"16\" width=\"4\" height=\"1\" fill=\"{bright}\"/>");
        sb.Append($"<rect x=\"16\" y=\"13\" width=\"1\" height=\"4\" fill=\"{bright}\"/>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static (string dark, string mid, string bright, string dim) ComputeColors(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) return ("#111", "#555", "#ccc", "#333");
        double r = Convert.ToInt32(h[0..2], 16) / 255.0;
        double g = Convert.ToInt32(h[2..4], 16) / 255.0;
        double b = Convert.ToInt32(h[4..6], 16) / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d   = max - min;
        double hue = 0;
        if (d > 0.01)
        {
            if      (max == r) hue = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) hue = ((b - r) / d + 2) / 6.0;
            else               hue = ((r - g) / d + 4) / 6.0;
        }

        return (
            HslToHex(hue, 0.55, 0.10),   // dark — deep terminal background / outlines
            HslToHex(hue, 0.55, 0.40),   // mid  — armor body
            HslToHex(hue, 0.85, 0.72),   // bright — lenses / highlights / frame brackets
            HslToHex(hue, 0.35, 0.22)    // dim  — outer border stroke
        );
    }

    private static string HslToHex(double hue, double s, double l)
    {
        if (s < 0.01) { int v = (int)Math.Round(l * 255); return $"#{v:X2}{v:X2}{v:X2}"; }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        static double C(double p, double q, double t)
        {
            t = ((t % 1) + 1) % 1;
            if (t < 1.0/6) return p + (q - p) * 6 * t;
            if (t < 0.5)   return q;
            if (t < 2.0/3) return p + (q - p) * (2.0/3 - t) * 6;
            return p;
        }
        int r = (int)Math.Round(C(p, q, hue + 1.0/3) * 255);
        int g = (int)Math.Round(C(p, q, hue) * 255);
        int b = (int)Math.Round(C(p, q, hue - 1.0/3) * 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
