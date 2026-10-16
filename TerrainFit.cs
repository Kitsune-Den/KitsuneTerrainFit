using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

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
/// Density is sampled at block centres, so the surface sits at the linear
/// zero-crossing between a voxel's centre and the centre above it:
/// t = -d / (u - d), measured up from the centre. t = 0.5 is the top of the
/// block, which is where flat natural ground sits (t ~ 0.504 in game).
/// Solving for d gives d = -t * u / (1 - t).
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

    /// <summary>The eight spaces around a block on its own level: sides then corners.</summary>
    public static readonly Vector3i[] Around =
    {
        new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0), new Vector3i(0, 0, 1), new Vector3i(0, 0, -1),
        new Vector3i(1, 0, 1), new Vector3i(-1, 0, 1), new Vector3i(1, 0, -1), new Vector3i(-1, 0, -1),
    };

    /// <summary>Keep the slope a hair under the top edge rather than exactly on it.</summary>
    private const double EdgeMargin = 0.01;

    /// <summary>
    /// Highest the fill may bring the ground inside a block (t, see class
    /// notes; 0.5 is the top face). The game works out what you're aiming at
    /// with a physics ray. When the ground mesh sits on or over a block's top
    /// face the ray hits the ground first and the hit lands on the dirt under
    /// the block, so it can't be picked up, upgraded or repaired. 0.45 keeps
    /// the top face a few centimetres proud of the ground.
    /// </summary>
    public const double MaxFillTop = 0.45;

    /// <summary>
    /// Skip reason when an upright shape is involved and the ground isn't
    /// level enough for a fill that stays at or under the ground around it.
    /// </summary>
    public const string NotLevel = "not-level-ground";

    /// <summary>A face whose normal is this close to level counts as a vertical wall.</summary>
    private const float WallTolerance = 0.02f;

    /// <summary>A flat face this close to the block's bottom or top counts as its floor or lid.</summary>
    private const float EndTolerance = 0.01f;

    private static readonly Vector3 BlockCentre = new Vector3(0.5f, 0.5f, 0.5f);

    /// <summary>IsUpright per block type and rotation; walking the mesh isn't free.</summary>
    private static readonly Dictionary<int, bool> uprightCache = new Dictionary<int, bool>();

    /// <summary>
    /// True for blocks whose geometry fully covers their voxel, so any terrain
    /// surface we pull inside them stays hidden.
    /// </summary>
    public static bool IsFittableCube(BlockValue bv, out string reason) => IsFittableCube(bv, out reason, out _);

    /// <param name="maxTop">
    /// Highest surface crossing (t, see class notes) the fill may reach inside
    /// this block. 1 for full cubes, which hide anything; lower for blocks like
    /// farm plots whose soil sits below the rim.
    /// </param>
    public static bool IsFittableCube(BlockValue bv, out string reason, out double maxTop)
    {
        maxTop = 1.0;
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

        // Blocks like farm plots aren't six full faces (open soil top, sides
        // that cull against neighbours) but their walls still hide the fill.
        reason = null;
        if (TerrainFitConfig.IsAlsoFit(block.GetBlockName()))
        {
            maxTop = TerrainFitConfig.AlsoFitHeight - 0.5;
            return true;
        }

        reason = "not-a-full-cube";
        const int allSixFaces = 63;
        if ((block.shape.getFacesDrawnFullBitfield(bv) & allSixFaces) != allSixFaces) return false;

        reason = null;
        return true;
    }

    /// <summary>
    /// True for blocks that run straight up and down at their current
    /// rotation: every surface is a vertical wall, or flat on the block's
    /// bottom or top. Rounded corners, pillars, quarter rounds, poles, and
    /// full cubes too. Every spot inside one is either solid from bottom to
    /// top or open from bottom to top, so ground at its natural height ends up
    /// hidden inside the block or sitting in open air, the way it was before
    /// the block went down. A step, ramp, plate or a shape lying on its side
    /// has a surface partway up that ground could poke through.
    /// </summary>
    public static bool IsUpright(BlockValue bv)
    {
        if (bv.isair || bv.isTerrain || bv.isWater || bv.ischild) return false;
        var block = bv.Block;
        if (block == null || !(block.shape is BlockShapeNew shape)) return false;
        if (block.HasTileEntity || block.isMultiBlock) return false;
        if (block.MeshIndex != MeshDescription.MESH_OPAQUE) return false;

        int rotation = bv.rotation & 31;
        int key = (bv.type << 5) | rotation;
        if (!uprightCache.TryGetValue(key, out bool upright))
        {
            upright = IsUprightMesh(shape, rotation);
            uprightCache[key] = upright;
        }
        return upright;
    }

    private static bool IsUprightMesh(BlockShapeNew shape, int rotation)
    {
        var meshes = shape.visualMeshes;
        if (meshes == null) return false;

        // Same block-local transform the game uses for shape bounds.
        var q = BlockShapeNew.GetRotationStatic(rotation);
        int faces = 0;
        foreach (var mesh in meshes)
        {
            if (mesh == null) continue;
            var v = mesh.Vertices;
            var idx = mesh.Indices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                var a = q * (v[idx[i]] - BlockCentre) + BlockCentre;
                var b = q * (v[idx[i + 1]] - BlockCentre) + BlockCentre;
                var c = q * (v[idx[i + 2]] - BlockCentre) + BlockCentre;
                var normal = Vector3.Cross(b - a, c - a);
                float length = normal.magnitude;
                if (length < 1e-6f) continue;
                faces++;

                float up = Mathf.Abs(normal.y / length);
                if (up < WallTolerance) continue;
                if (up > 1f - WallTolerance)
                {
                    float low = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                    float high = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                    if (high < EndTolerance || low > 1f - EndTolerance) continue;
                }
                return false;
            }
        }
        return faces > 0;
    }

    /// <summary>
    /// For an upright shape placed into open space: keep the density the
    /// space already had instead of vanilla's full air. Natural air just
    /// above the ground sits well under full air, and resetting it to full air
    /// is what pulls the ground down into the trench. Keeping it leaves the
    /// ground exactly where it was, including in the cut-away of a rounded
    /// corner, where a cube's fill would show as a lump of dirt.
    /// </summary>
    public static FitResult Keep(WorldBase world, Vector3i pos, ICollection<Vector3i> pending)
    {
        int d = world.GetDensity(pos.x, pos.y, pos.z);
        if (d >= MarchingCubes.DensityAir)
            return FitResult.Skip("already-air", "no natural ground density to keep");

        // A floor, step or slab beside it would lose the dip vanilla gives it.
        foreach (var offset in Around)
        {
            var n = pos + offset;
            if (pending.Contains(n)) continue;
            var nbv = world.GetBlock(n.x, n.y, n.z);
            if (ShowsFill(nbv) && !IsUpright(nbv))
                return FitResult.Skip("next-to-partial-block");
        }

        return new FitResult
        {
            Apply = true,
            Density = (sbyte)d,
            Mode = "KEEP",
            Detail = string.Format(CultureInfo.InvariantCulture, "ground density {0}", d),
        };
    }

    /// <summary>
    /// Works out the density for a full cube at <paramref name="pos"/>.
    /// <paramref name="pending"/> holds positions changing in the same batch;
    /// their current contents are about to be replaced, so they're ignored.
    /// <paramref name="levelOnly"/> allows only the level SURFACE fit, for an
    /// upright shape whose cut-away would show anything higher.
    /// </summary>
    public static FitResult Solve(WorldBase world, Vector3i pos, ICollection<Vector3i> pending, double maxTop = 1.0, bool levelOnly = false)
    {
        if (maxTop <= 0) return FitResult.Skip("fill-height-off");

        var above = pos + Up;
        if (pending.Contains(above) || !IsOpen(world, above))
            return FitResult.Skip("covered", "the space above isn't open");
        int u = world.GetDensity(above.x, above.y, above.z);

        // A fill shapes the ground in the half of each neighbouring space
        // next to this block: outdoors that's the ramp that closes the trench.
        // A floor, step, slab or door there can't hide it, so dirt would poke
        // up through it. Only fit when every neighbour is open, ground, or a
        // block that covers its whole space. An upright shape like a rounded
        // corner shows the fill in its cut-away, which is fine for a level
        // fill (that's just the ground carrying on) but not for one that
        // raises the ground above its surroundings.
        foreach (var offset in Around)
        {
            var n = pos + offset;
            if (pending.Contains(n)) continue;
            var nbv = world.GetBlock(n.x, n.y, n.z);
            if (!ShowsFill(nbv)) continue;
            if (!IsUpright(nbv)) return FitResult.Skip("next-to-partial-block");
            levelOnly = true;
        }

        int surfaceSides = 0, wallSides = 0, lowerSides = 0, surfaceCorners = 0;
        double weighted = 0, weight = 0;
        double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
        double highestLower = double.NegativeInfinity;
        double lowestCorner = double.PositiveInfinity;
        // Ground height per side for the top-edge limit; anything that isn't a
        // surface counts as 0, which is the stricter assumption.
        var sideHeight = new double[4];
        int wallCorners = 0;

        for (int i = 0; i < Sides.Length; i++)
        {
            var n = pos + Sides[i];
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
                    sideHeight[i] = h;
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

        for (int i = 0; i < Corners.Length; i++)
        {
            var n = pos + Corners[i];
            if (pending.Contains(n) || pending.Contains(n + Up)) continue;
            if (IsWater(world, n) || IsWater(world, n + Up))
                return FitResult.Skip("water");

            if (IsSolidTerrain(world, n) && IsSolidTerrain(world, n + Up))
            {
                wallCorners++;
            }
            else if (IsSolidTerrain(world, n) && IsOpen(world, n + Up))
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
            "sides {0}, corners {1}, walls {2}, lower {3}", surfaceSides, surfaceCorners, wallSides + wallCorners, lowerSides);

        // Open-topped blocks (maxTop < 1) and hillside="clear" keep the top
        // edge visible. hillside="embed" lets the hill wash over a cube's
        // edge so it looks sunk into the slope.
        bool keepEdgeClear = maxTop < 1.0 || !TerrainFitConfig.EmbedInHillside;

        // Against a terrain wall vanilla already keeps the edge clear: air
        // density puts the wall's surface exactly on the block's face. Any
        // other value tilts that surface; +1 pulls the hill in over the edge,
        // which is the embedded look.
        if (wallSides > 0 && !keepEdgeClear)
            return levelOnly
                ? FitResult.Skip(NotLevel, contacts)
                : new FitResult { Apply = true, Density = 1, Mode = "WALL", Detail = contacts };
        if (wallSides > 0 || (wallCorners > 0 && keepEdgeClear))
            return FitResult.Skip("against-a-wall", contacts);

        // The mesh joins neighbouring columns with straight slopes, so at the
        // block's face the ground sits halfway between the neighbour's height
        // and ours. Keep that at or under the top edge (t = 0.5 at the face):
        //   (h_side + t) / 2 <= 0.5
        // Otherwise ground higher than the block, like an uphill neighbour,
        // spills over the block and buries its edge. Corners aren't limited:
        // a diagonal limit held fills right down on gentle slopes and brought
        // the trench back, for a corner that's barely visible either way.
        double ceiling = 1.0;
        if (keepEdgeClear)
        {
            for (int i = 0; i < 4; i++)
                ceiling = Math.Min(ceiling, 1.0 - sideHeight[i]);
            maxTop = Math.Min(maxTop, ceiling - EdgeMargin);
        }
        maxTop = Math.Min(maxTop, MaxFillTop);
        if (maxTop <= 0.005)
            return FitResult.Skip("would-bury", contacts + string.Format(CultureInfo.InvariantCulture, ", room {0:F3}", ceiling));

        if (surfaceSides > 0)
        {
            double target = Math.Min(weighted / weight, lowest + MaxRiseAboveLowest);
            bool canForce = surfaceSides >= 2 && highest - lowest <= MaxSpreadToForce;
            return Fitted("SURFACE", Math.Min(target, maxTop), u, canForce ? 128 : UnforcedLimit, Math.Round,
                contacts + string.Format(CultureInfo.InvariantCulture, ", ground {0:F3}-{1:F3}", lowest, highest));
        }

        // Everything below raises the ground above what's around it.
        if (levelOnly && (lowerSides > 0 || surfaceCorners > 0))
            return FitResult.Skip(NotLevel, contacts);

        // Cube resting a little above the ground: raise the ground under its
        // edges to meet it. The closer the ground, the more fill.
        if (lowerSides > 0)
        {
            double closeness = Clamp((highestLower + LowerReach) / LowerReach, 0, 1);
            return Fitted("LOWER", Math.Min(LowerFillMax * closeness, maxTop), u, UnforcedLimit, Math.Floor, contacts);
        }

        if (surfaceCorners > 0)
        {
            double target = Clamp(lowestCorner - MaxRiseAboveLowest, 0, Math.Min(CornerFillMax, maxTop));
            return Fitted("CORNER", target, u, UnforcedLimit, Math.Floor, contacts);
        }

        return FitResult.Skip("no-ground-contact", contacts);
    }

    /// <summary>
    /// For a fitted block whose fill reaches over <see cref="MaxFillTop"/>
    /// (fitted before the cap existed, or baked into a prefab): the density
    /// that brings the ground back down under its top face.
    /// </summary>
    public static bool IsOverfilled(WorldBase world, Vector3i pos, out sbyte lowered, out bool force)
    {
        lowered = 0;
        force = false;
        int d = world.GetDensity(pos.x, pos.y, pos.z);
        if (d >= 0) return false;
        var above = pos + Up;
        if (!IsOpen(world, above)) return false;
        int u = world.GetDensity(above.x, above.y, above.z);
        if (-d / (double)(u - d) <= MaxFillTop + 0.005) return false;

        int magnitude = Math.Min((int)Math.Floor(MaxFillTop * u / (1.0 - MaxFillTop)), 128);
        if (magnitude < 1) return false;
        lowered = (sbyte)-magnitude;
        force = magnitude > UnforcedLimit;
        return true;
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

    /// <summary>
    /// A block that wouldn't hide ground shaped into its space: not air or
    /// grass-like cover, not terrain or water, and not a full opaque cube or
    /// an alsoFit block. Floors, steps, slabs, doors, glass...
    /// </summary>
    public static bool ShowsFill(BlockValue bv)
    {
        if (bv.isair || bv.isTerrain || bv.isWater) return false;
        var block = bv.Block;
        if (block == null || block.CanBlocksReplaceOrGroundCover()) return false;
        // Flowers, crops, bushes, rocks: the ground around them is ground.
        if (block is BlockPlant || block.IsTerrainDecoration) return false;
        if (bv.ischild) return true;
        return !IsFittableCube(bv, out _);
    }

    private static bool IsSolidTerrain(WorldBase world, Vector3i p)
    {
        var bv = world.GetBlock(p.x, p.y, p.z);
        return bv.isTerrain && world.GetDensity(p.x, p.y, p.z) < 0;
    }

    /// <summary>
    /// Air-side as far as the terrain mesh is concerned: positive density and
    /// not terrain or water. Includes grass, crops and unfitted blocks, which
    /// the mesher treats exactly like air.
    /// </summary>
    public static bool IsOpen(WorldBase world, Vector3i p)
    {
        if (world.GetDensity(p.x, p.y, p.z) <= 0) return false;
        var bv = world.GetBlock(p.x, p.y, p.z);
        return !bv.isTerrain && !bv.isWater && !world.IsWater(p);
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
