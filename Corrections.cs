using System.Collections.Generic;

/// <summary>
/// Builds and sends density-only block changes.
///
/// A density-only change carries no block value, so on arrival vanilla's
/// ChangeBlocks sees the "old" block as air. Air can be replaced, so it then
/// resets the position's paint to blank. To stop that wiping painted cubes,
/// every change here also carries the block's current paint. The reset then
/// writes back what was already there.
/// </summary>
public static class Corrections
{
    /// <summary>NetPackageSetBlock writes its count as a short; stay well under it.</summary>
    private const int BatchSize = 1000;

    public static BlockChangeInfo DensityOnly(Vector3i pos, sbyte density, bool force)
    {
        var change = new BlockChangeInfo(new BlockValueRef(pos), density, force);
        var cache = GameManager.Instance.World?.ChunkCache;
        if (cache != null)
        {
            change.bChangeTexture = true;
            change.textureFull = cache.GetTextureFullArray(pos);
        }
        return change;
    }

    /// <summary>Sends the fitted densities in <paramref name="fitted"/> to one client only.</summary>
    public static void SendTo(WorldBase world, int entityId, List<BlockChangeInfo> fitted)
    {
        var changes = new List<BlockChangeInfo>(fitted.Count);
        foreach (var f in fitted)
            if (f.blockValueRef.TryGetBlockPos(out var pos))
                changes.Add(DensityOnly(pos, f.density, f.bForceDensity));
        if (changes.Count == 0) return;

        var package = NetPackageManager.GetPackage<NetPackageSetBlock>().Setup(null, changes, -1);
        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(package, false, entityId);
    }

    /// <summary>Applies density changes on the server and relays them to every client.</summary>
    public static void ApplyEverywhere(List<BlockChangeInfo> changes)
    {
        for (int i = 0; i < changes.Count; i += BatchSize)
            GameManager.Instance.SetBlocksRPC(changes.GetRange(i, System.Math.Min(BatchSize, changes.Count - i)));
    }
}
