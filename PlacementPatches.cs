using System;
using System.Collections.Generic;
using HarmonyLib;

/// <summary>
/// Fits cubes as they're placed, on the server.
///
/// Two ways a block change reaches the server:
///  - a player places it: their client applies it locally, then sends a
///    NetPackageSetBlock. The server applies it and relays it to every OTHER
///    client (SetBlocksOnClients excludes the sender).
///  - the server makes it itself via GameManager.SetBlocksRPC, which relays to
///    every client.
///
/// We adjust the change list before either runs, so the server's world and the
/// relay carry the fitted density. For player placements we then send the
/// placing player a density-only correction, because the relay skips them and
/// their client would otherwise keep the vanilla density until the chunk reloads.
/// </summary>
public static class PlacementPatches
{
    public static void Register(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(Patch_NetPackageSetBlock_ProcessPackage)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_GameManager_SetBlocksRPC)).Patch();
    }

    private static bool IsServer
    {
        get
        {
            var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            return cm != null && cm.IsServer;
        }
    }

    [HarmonyPatch(typeof(NetPackageSetBlock), nameof(NetPackageSetBlock.ProcessPackage))]
    private static class Patch_NetPackageSetBlock_ProcessPackage
    {
        private static void Prefix(NetPackageSetBlock __instance, World _world, out List<BlockChangeInfo> __state)
        {
            __state = null;
            if (!TerrainFitConfig.Enabled || !IsServer || _world == null) return;
            // Vanilla drops packages whose sender doesn't match; don't fit those either.
            if (!__instance.ValidUserIdForSender(__instance.persistentPlayerId)
                || !__instance.ValidEntityIdForSender(__instance.localPlayerThatChanged)) return;
            try
            {
                __state = FitPlacements(_world, __instance.blockChanges);
            }
            catch (Exception ex)
            {
                Log.Error($"[KitsuneTerrainFit] Fitting a player placement failed; placement left vanilla. {ex}");
            }
        }

        private static void Postfix(NetPackageSetBlock __instance, World _world, List<BlockChangeInfo> __state)
        {
            if (__state == null || __state.Count == 0) return;
            int placer = __instance.localPlayerThatChanged;
            if (placer < 0) return;
            try
            {
                Corrections.SendTo(_world, placer, __state);
            }
            catch (Exception ex)
            {
                Log.Error($"[KitsuneTerrainFit] Couldn't send the fitted density back to entity {placer}; they'll see it after the chunk reloads. {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.SetBlocksRPC))]
    private static class Patch_GameManager_SetBlocksRPC
    {
        private static void Prefix(GameManager __instance, List<BlockChangeInfo> _changes)
        {
            if (!TerrainFitConfig.Enabled || !IsServer || __instance.World == null) return;
            try
            {
                FitPlacements(__instance.World, _changes);
            }
            catch (Exception ex)
            {
                Log.Error($"[KitsuneTerrainFit] Fitting a server placement failed; placement left vanilla. {ex}");
            }
        }
    }

    /// <summary>
    /// Sets a fitted density on every change in <paramref name="changes"/> that
    /// places a full cube into open space next to the ground. Returns the
    /// changes it adjusted.
    /// </summary>
    private static List<BlockChangeInfo> FitPlacements(WorldBase world, List<BlockChangeInfo> changes)
    {
        if (changes == null || changes.Count == 0) return null;

        HashSet<Vector3i> pending = null;
        List<BlockChangeInfo> fitted = null;
        List<BlockChangeInfo> resets = null;

        foreach (var change in changes)
        {
            if (change == null || !change.bChangeBlockValue) continue;
            if (!change.blockValueRef.TryGetBlockPos(out var pos)) continue;
            if (pos.y < 2 || pos.y > 253) continue;

            // A floor, step or similar going in next to an already-fitted cube
            // would show that cube's fill poking up through it (walls built
            // before floors). Put those neighbours back to vanilla. An upright
            // shape like a rounded corner shows the fill in its cut-away
            // instead: a level fill is just the ground carrying on, so those
            // stay, and anything higher drops to the natural ground density
            // the new block is about to keep.
            if (TerrainFit.ShowsFill(change.blockValue))
            {
                pending ??= PendingPositions(changes);
                bool upright = TerrainFitConfig.KeepShapes && TerrainFit.IsUpright(change.blockValue);
                sbyte reset = MarchingCubes.DensityAir;
                if (upright)
                {
                    int here = world.GetDensity(pos.x, pos.y, pos.z);
                    if (here > 0 && here < MarchingCubes.DensityAir) reset = (sbyte)here;
                }
                foreach (var offset in TerrainFit.Around)
                {
                    var n = pos + offset;
                    if (pending.Contains(n)) continue;
                    var nbv = world.GetBlock(n.x, n.y, n.z);
                    int nd = world.GetDensity(n.x, n.y, n.z);
                    bool isCube = TerrainFit.IsFittableCube(nbv, out _, out var nMaxTop);
                    bool isUpright = TerrainFit.IsUpright(nbv);
                    bool isFitted = nd <= 1 && (isCube || isUpright);
                    // Kept natural ground is fine beside another upright shape, not beside a floor or step.
                    bool isKept = !upright && nd > 1 && nd < MarchingCubes.DensityAir && isUpright;
                    if (!isFitted && !isKept) continue;
                    if (upright && isFitted)
                    {
                        var level = TerrainFit.Solve(world, n, pending, isCube ? nMaxTop : 1.0, levelOnly: true);
                        if (level.Apply && level.Mode == "SURFACE") continue;
                    }
                    (resets ??= new List<BlockChangeInfo>()).Add(Corrections.DensityOnly(n, reset, false));
                    pending.Add(n);
                    Log.Out($"[KitsuneTerrainFit] unfit {Name(nbv)} at {n}: {Name(change.blockValue)} placed beside it by entity {change.changedByEntityId}{(reset != MarchingCubes.DensityAir ? $", density {reset} to match it" : "")}");
                }
            }

            // Only into space that's still air-side: open ground, or a block
            // that was never fitted (e.g. a frame being upgraded). Anything
            // already fitted keeps its density.
            if (!TerrainFit.IsOpen(world, pos)) continue;
            // Vanilla's Block.PlaceBlock always sends DensityAir with a
            // placement, so only a solid density means someone upstream chose one.
            if (change.bChangeDensity && change.density <= 0) continue;

            FitResult fit;
            if (TerrainFit.IsFittableCube(change.blockValue, out var notCube, out var maxTop))
            {
                pending ??= PendingPositions(changes);
                pending.Remove(pos);
                fit = TerrainFit.Solve(world, pos, pending, maxTop);
                // Beside a rounded corner or pillar on uneven ground: keep the natural ground for both.
                if (!fit.Apply && fit.Mode == TerrainFit.NotLevel && TerrainFitConfig.KeepShapes && change.bChangeDensity)
                    fit = TerrainFit.Keep(world, pos, pending);
                pending.Add(pos);
            }
            else if (TerrainFitConfig.KeepShapes && TerrainFit.IsUpright(change.blockValue))
            {
                // Level ground gets the same fill as a cube; the cut-away just
                // shows the ground carrying on. Otherwise keep what's there.
                pending ??= PendingPositions(changes);
                pending.Remove(pos);
                fit = TerrainFit.Solve(world, pos, pending, 1.0, levelOnly: true);
                // A change that doesn't set density already leaves the ground as it is.
                if (!fit.Apply && change.bChangeDensity)
                    fit = TerrainFit.Keep(world, pos, pending);
                pending.Add(pos);
            }
            else
            {
                var bv = change.blockValue;
                // Digging, terrain and water aren't builds; not worth a line even in verbose.
                if (TerrainFitConfig.Verbose && !bv.isair && !bv.isTerrain && !bv.isWater)
                    Log.Out($"[KitsuneTerrainFit] skip {Name(change.blockValue)} at {pos} by entity {change.changedByEntityId}: {(notCube == "not-a-full-cube" && TerrainFitConfig.KeepShapes ? "not-a-cube-or-upright-shape" : notCube)}");
                continue;
            }

            if (!fit.Apply)
            {
                if (TerrainFitConfig.Verbose)
                    Log.Out($"[KitsuneTerrainFit] skip {Name(change.blockValue)} at {pos} by entity {change.changedByEntityId}: {fit.Mode}{(fit.Detail != null ? " (" + fit.Detail + ")" : "")}");
                continue;
            }

            change.bChangeDensity = true;
            change.density = fit.Density;
            change.bForceDensity = fit.Force;
            (fitted ??= new List<BlockChangeInfo>()).Add(change);

            Log.Out($"[KitsuneTerrainFit] fit {Name(change.blockValue)} at {pos} by entity {change.changedByEntityId}: {fit.Mode} density {fit.Density}{(fit.Force ? " (forced)" : "")} ({fit.Detail})");
        }

        if (resets != null)
        {
            // Same batch, so the server applies them and relays them with the
            // placement; the placer gets them through the correction.
            changes.AddRange(resets);
            (fitted ??= new List<BlockChangeInfo>()).AddRange(resets);
        }

        return fitted;
    }

    private static HashSet<Vector3i> PendingPositions(List<BlockChangeInfo> changes)
    {
        var set = new HashSet<Vector3i>();
        foreach (var c in changes)
            if (c != null && c.bChangeBlockValue && c.blockValueRef.TryGetBlockPos(out var p))
                set.Add(p);
        return set;
    }

    internal static string Name(BlockValue bv) => bv.Block?.GetBlockName() ?? $"block#{bv.type}";
}
