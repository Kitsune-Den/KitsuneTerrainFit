using HarmonyLib;

/// <summary>
/// Entry point for KitsuneTerrainFit.
///
/// Fits full cubes to the ground as they're placed, so terrain meets them flush
/// instead of dipping into a trench at their base. Server-side only: the
/// fitted density travels to clients in the normal block-change packets, so
/// joining players don't need the mod.
/// </summary>
public class KitsuneTerrainFitMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        TerrainFitConfig.Load(_modInstance.Path);

        var harmony = new Harmony("com.adainthelab.kitsuneterrainfit");
        PlacementPatches.Register(harmony);

        Log.Out(TerrainFitConfig.Enabled
            ? $"[KitsuneTerrainFit] Loaded - new cubes are fitted to the terrain around them{(TerrainFitConfig.Verbose ? " (verbose logging on)" : "")}. Admins: 'tfit' to fit existing builds."
            : "[KitsuneTerrainFit] Loaded but disabled in Settings.xml - placement is vanilla. 'tfit refit' still works.");
    }
}
