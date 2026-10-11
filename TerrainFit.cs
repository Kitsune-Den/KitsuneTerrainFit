using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// What to do with one cube: the density to give it, or why it's left alone.
/// </summary>
public struct FitResult
{
    public bool Apply;
    public sbyte Density;
    public bool Force;
    /// <summary>SURFACE / LOWER / CORNER / WALL when applied, otherwise the skip reason.</summary>
    public string Mode;
    public string Detail;

    public static FitResult Skip(string reason, string detail = null) =>
        new FitResult { Apply = false, Mode = reason, Detail = detail };
}

/// <summary>
/// The terrain-fit math.
///
/// 7D2D meshes terrain with marching cubes over per-voxel density (negative =
/// solid, positive = air). A placed cube keeps the air density of the voxel it
/// went into (+127), so the mesher treats it as a hole: the ground next to it
/// dips toward the cube and leaves a visible trench at its base.
///
/// Giving the cube a negative density makes the mesher treat it as ground. We
/// pick the value so the surface crossing between the cube and the open voxel
/// above it lands at the same height as the ground around it. That keeps the
/// terrain flat right up to the cube, with the extra bit of surface hidden
/// inside the cube itself.
///
/// Surface height inside a voxel is the linear zero-crossing between that
/// voxel's density and the one above: t = -d / (u - d). Solving for d gives
/// d = -t * u / (1 - t).
/// </summary>
public static class TerrainFit
{
    /// <summary>
    /// ChunkCluster.SetBlock clamps a non-terrain block's density to
    /// MarchingCubes.DensityTerrainHi (-100) unless the change is forced.
    /// </summary>
    private const int UnforcedLimit = 100;

    /// <summary>Never let the fit rise more than this above the lowest neighbour (it would poke out as a lip).</summary>
    private const double MaxRiseAboveLowest = 0.05;

    /// <summary>Neighbour heights must agree this closely before we'll force past the -100 clamp.</summary>
    private const double MaxSpreadToForce = 0.10;

    private const double SideWeight = 2.0;
    private const double CornerWeight = 1.0;

    /// <summary>Cube sitting just above lower ground: most fill we'll add, and how far down we'll still reach.</summary>
    private const double LowerFillMax = 0.30;
    private const double LowerReach = 0.60;

    /// <summary>Cube touching ground only diagonally: most fill we'll add.</summary>
    private const double CornerFillMax = 0.15;

    private static readonly Vector3i Up = new Vector3i(0, 1, 0);
    private static readonly Vector3i Down = new Vector3i(0, -1, 0);

    private static readonly Vector3i[] Sides =
    {
        new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0), new Vector3i(0, 0, 1), new Vector3i(0, 0, -1),
    };

    private static readonly Vector3i[] Corners =
    {
        new Vector3i(1, 0, 1), new Vector3i(-1, 0, 1), new Vector3i(1, 0, -1), new Vector3i(-1, 0, -1),
    };

    /// <summary>
    /// True for blocks whose geometry fully covers their voxel, so any terrain
    /// surface we pull inside them stays hidden.
    /// </summary>
    public static bool IsFittableCube(BlockValue bv, out string reason)
    {
        reason = "not-a-building-block";
        if (bv.isair || bv.isTerrain || bv.isWater || bv.ischild) return false;

        var block = bv.Block;
        reason = "not-a-shaped-block";
        if (block == null || !(block.shape is BlockShapeNew)) return false;

        reason = "tile-entity-or-multiblock";
        if (block.HasTileEntity || block.isMultiBlock) return false;

        // Glass and other see-through cubes would show the terrain we tuck inside them.
        reason = "not-opaque";
        if (block.MeshIndex != MeshDescription.MESH_OPAQUE) return false;

        reason = "not-a-full-cube";
        const int allSixFaces = 63;
        if ((block.shape.getFacesDrawnFullBitfield(bv) & allSixFaces) != allSixFaces) return false;

        reason = null;
        return true;
    }

    /// <summary>
    /// Works out the density for a full cube at <paramref name="pos"/>.
    /// <paramref name="pending"/> holds positions changing in the same batch;
    /// their current contents are about to be replaced, so they're ignored.
    /// </summary>
    public static FitResult Solve(WorldBase world, Vector3i pos, ICollection<Vector3i> pending)
    {
        var above = pos + Up;
        if (pending.Contains(above) || !IsOpen(world, above))
            return FitResult.Skip("covered", "the space above isn't open");
        int u = world.GetDensity(above.x, above.y, above.z);

        int surfaceSides = 0, wallSides = 0, lowerSides = 0, surfaceCorners = 0;
        double weighted = 0, weight = 0;
        double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
        double highestLower = double.NegativeInfinity;
        double lowestCorner = double.PositiveInfinity;

        foreach (var offset in Sides)
        {
            var n = pos + offset;
            if (pending.Contains(n)) continue;
            if (IsWater(world, n) || IsWater(world, n + Up))
                return FitResult.Skip("water");

            if (IsSolidTerrain(world, n))
            {
                if (IsSolidTerrain(world, n + Up))
                {
                    wallSides++;
                }
                else if (!pending.Contains(n + Up) && IsOpen(world, n + Up))
                {
                    double h = Crossing(world, n, n + Up);
                    surfaceSides++;
                    weighted += SideWeight * h;
                    weight += SideWeight;
                    lowest = Math.Min(lowest, h);
                    highest = Math.Max(highest, h);
                }
            }
            else if (IsOpen(world, n) && !pending.Contains(n + Down) && IsSolidTerrain(world, n + Down))
            {
                lowerSides++;
                highestLower = Math.Max(highestLower, Crossing(world, n + Down, n) - 1.0);
            }
        }

        foreach (var offset in Corners)
        {
            var n = pos + offset;
            if (pending.Contains(n) || pending.Contains(n + Up)) continue;
            if (IsWater(world, n) || IsWater(world, n + Up))
                return FitResult.Skip("water");

            if (IsSolidTerrain(world, n) && IsOpen(world, n + Up))
            {
                double h = Crossing(world, n, n + Up);
                surfaceCorners++;
                weighted += CornerWeight * h;
                weight += CornerWeight;
                lowest = Math.Min(lowest, h);
                highest = Math.Max(highest, h);
                lowestCorner = Math.Min(lowestCorner, h);
            }
        }

        string contacts = string.Format(CultureInfo.InvariantCulture,
            "sides {0}, corners {1}, walls {2}, lower {3}", surfaceSides, surfaceCorners, wallSides, lowerSides);

        // Built into a cut in a hillside: pull the wall's surface into the cube
        // so there's no sliver of gap between the wall and the cube face.
        if (wallSides > 0)
            return new FitResult { Apply = true, Density = 1, Mode = "WALL", Detail = contacts };

        if (surfaceSides > 0)
        {
            double target = Math.Min(weighted / weight, lowest + MaxRiseAboveLowest);
            bool canForce = surfaceSides >= 2 && highest - lowest <= MaxSpreadToForce;
            return Fitted("SURFACE", target, u, canForce ? 128 : UnforcedLimit, Math.Round,
                contacts + string.Format(CultureInfo.InvariantCulture, ", ground {0:F3}-{1:F3}", lowest, highest));
        }

        // Cube resting a little above the ground: raise the ground under its
        // edges to meet it. The closer the ground, the more fill.
        if (lowerSides > 0)
        {
            double closeness = Clamp((highestLower + LowerReach) / LowerReach, 0, 1);
            return Fitted("LOWER", LowerFillMax * closeness, u, UnforcedLimit, Math.Floor, contacts);
        }

        if (surfaceCorners > 0)
        {
            double target = Clamp(lowestCorner - MaxRiseAboveLowest, 0, CornerFillMax);
            return Fitted("CORNER", target, u, UnforcedLimit, Math.Floor, contacts);
        }

        return FitResult.Skip("no-ground-contact", contacts);
    }

    private static FitResult Fitted(string mode, double target, int u, int limit, Func<double, double> round, string contacts)
    {
        if (target <= 0.005)
            return FitResult.Skip("ground-too-far", contacts);

        target = Math.Min(target, 0.99);
        int magnitude = (int)round(target * u / (1.0 - target));
        if (magnitude < 1) return FitResult.Skip("ground-too-far", contacts);
        magnitude = Math.Min(magnitude, limit);

        double top = magnitude / (double)(u + magnitude);
        return new FitResult
        {
            Apply = true,
            Density = (sbyte)Math.Max(-magnitude, sbyte.MinValue),
            Force = magnitude > UnforcedLimit,
            Mode = mode,
            Detail = contacts + string.Format(CultureInfo.InvariantCulture, ", top {0:F3}", top),
        };
    }

    private static bool IsSolidTerrain(WorldBase world, Vector3i p)
    {
        var bv = world.GetBlock(p.x, p.y, p.z);
        return bv.isTerrain && world.GetDensity(p.x, p.y, p.z) < 0;
    }

    /// <summary>Air or grass-like cover with positive (air-side) density.</summary>
    public static bool IsOpen(WorldBase world, Vector3i p)
    {
        if (world.GetDensity(p.x, p.y, p.z) <= 0) return false;
        var bv = world.GetBlock(p.x, p.y, p.z);
        if (bv.isTerrain || bv.isWater) return false;
        if (bv.isair) return true;
        var block = bv.Block;
        return block != null && block.CanBlocksReplaceOrGroundCover();
    }

    private static bool IsWater(WorldBase world, Vector3i p) =>
        world.IsWater(p) || world.GetBlock(p.x, p.y, p.z).isWater;

    /// <summary>Height (0..1) of the zero-crossing between a solid voxel and the open voxel above it.</summary>
    private static double Crossing(WorldBase world, Vector3i solid, Vector3i open)
    {
        double d0 = world.GetDensity(solid.x, solid.y, solid.z);
        double d1 = world.GetDensity(open.x, open.y, open.z);
        return -d0 / (d1 - d0);
    }

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));
}
