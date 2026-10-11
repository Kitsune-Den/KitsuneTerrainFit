using System;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Server-side settings for KitsuneTerrainFit, read once at mod load from
/// <c>Settings.xml</c> in the mod folder root.
///
/// Plain regex read like KitsuneTrapXP's, so a malformed file can never throw
/// past the try/catch ~ worst case we log and keep the defaults.
/// </summary>
public static class TerrainFitConfig
{
    /// <summary>Fit newly placed cubes. Off = vanilla placement.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>Log skipped placements too, not just fitted ones.</summary>
    public static bool Verbose { get; set; }

    public const string DefaultAlsoFit = "farmPlotBlock*";

    /// <summary>
    /// Block names fitted even though they aren't six full faces. Comma
    /// separated; '*' matches any run of characters.
    /// </summary>
    public static string AlsoFit { get; private set; } = DefaultAlsoFit;

    private static Regex alsoFitRegex = Compile(DefaultAlsoFit);

    public const double DefaultAlsoFitHeight = 0.75;

    /// <summary>
    /// How far up an alsoFit block the ground may come, as a fraction of the
    /// block's height. 1 = flush with the top (buries a farm plot's soil),
    /// 0.5 or less = no fill.
    /// </summary>
    public static double AlsoFitHeight { get; private set; } = DefaultAlsoFitHeight;

    public static bool IsAlsoFit(string blockName) =>
        alsoFitRegex != null && blockName != null && alsoFitRegex.IsMatch(blockName);

    public static void Load(string modPath)
    {
        Enabled = true;
        Verbose = false;
        SetAlsoFit(DefaultAlsoFit);
        AlsoFitHeight = DefaultAlsoFitHeight;
        try
        {
            if (string.IsNullOrEmpty(modPath)) return;

            var path = Path.Combine(modPath, "Settings.xml");
            if (!File.Exists(path))
            {
                Log.Out("[KitsuneTerrainFit] No Settings.xml found; using defaults (enabled, quiet).");
                return;
            }

            // Drop comments first: the explanatory one mentions attribute
            // examples like alsoFit="" that would otherwise match first.
            var text = Regex.Replace(File.ReadAllText(path), "<!--.*?-->", "", RegexOptions.Singleline);
            Enabled = ReadBool(text, "enabled", true);
            Verbose = ReadBool(text, "verbose", false);
            var m = Regex.Match(text, "alsoFit\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            if (m.Success) SetAlsoFit(m.Groups[1].Value);
            AlsoFitHeight = ReadFraction(text, "alsoFitHeight", DefaultAlsoFitHeight);
            if (AlsoFit.Length > 0)
                Log.Out($"[KitsuneTerrainFit] Also fitting: {AlsoFit} (ground up to {AlsoFitHeight:0.##} of their height)");
        }
        catch (Exception ex)
        {
            Log.Warning($"[KitsuneTerrainFit] Failed to read Settings.xml ({ex.Message}); using defaults.");
        }
    }

    private static void SetAlsoFit(string patterns)
    {
        AlsoFit = (patterns ?? "").Trim();
        alsoFitRegex = Compile(AlsoFit);
    }

    private static Regex Compile(string patterns)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var raw in patterns.Split(','))
        {
            var p = raw.Trim();
            if (p.Length > 0) parts.Add(Regex.Escape(p).Replace("\\*", ".*"));
        }
        return parts.Count == 0 ? null : new Regex("^(?:" + string.Join("|", parts) + ")$", RegexOptions.IgnoreCase);
    }

    private static double ReadFraction(string text, string name, double fallback)
    {
        var m = Regex.Match(text, name + "\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (!m.Success) return fallback;
        if (double.TryParse(m.Groups[1].Value.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 1)
            return v;
        Log.Warning($"[KitsuneTerrainFit] Settings.xml '{name}' value '{m.Groups[1].Value}' isn't a number from 0 to 1; using {fallback}.");
        return fallback;
    }

    private static bool ReadBool(string text, string name, bool fallback)
    {
        var m = Regex.Match(text, name + "\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (!m.Success) return fallback;
        if (bool.TryParse(m.Groups[1].Value.Trim(), out var v)) return v;
        Log.Warning($"[KitsuneTerrainFit] Settings.xml '{name}' value '{m.Groups[1].Value}' isn't true/false; using {fallback}.");
        return fallback;
    }
}
