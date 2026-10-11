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

    public static void Load(string modPath)
    {
        Enabled = true;
        Verbose = false;
        try
        {
            if (string.IsNullOrEmpty(modPath)) return;

            var path = Path.Combine(modPath, "Settings.xml");
            if (!File.Exists(path))
            {
                Log.Out("[KitsuneTerrainFit] No Settings.xml found; using defaults (enabled, quiet).");
                return;
            }

            var text = File.ReadAllText(path);
            Enabled = ReadBool(text, "enabled", true);
            Verbose = ReadBool(text, "verbose", false);
        }
        catch (Exception ex)
        {
            Log.Warning($"[KitsuneTerrainFit] Failed to read Settings.xml ({ex.Message}); using defaults.");
        }
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
