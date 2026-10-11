using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Admin console command for KitsuneTerrainFit.
///
///   tfit refit [radius] [dry]           fit cubes already built near you
///   tfit refit x y z [radius] [dry]     same, around a point (telnet / web console)
///   tfit unfit [radius] [dry]           put cubes near you back to vanilla density
///   tfit unfit x y z [radius] [dry]
///   tfit verbose on|off                 log skipped placements too
///
/// Runs on the server. Changes are relayed to every connected client.
/// </summary>
public class ConsoleCmdTerrainFit : ConsoleCmdAbstract
{
    private const int DefaultRadius = 16;
    private const int MaxRadius = 64;
    /// <summary>Vertical reach either side of the centre. Builds rarely sit more than this off the ground.</summary>
    private const int VerticalReach = 16;

    public override string[] getCommands() => new[] { "terrainfit", "tfit" };

    public override string getDescription() => "KitsuneTerrainFit: fit existing builds to the terrain, or undo it.";

    public override string getHelp() =>
        "Usage:\n" +
        "  tfit refit [radius] [dry]          - fit full cubes near you that still have vanilla density\n" +
        "  tfit refit <x> <y> <z> [radius] [dry]\n" +
        "  tfit unfit [radius] [dry]          - reset fitted cubes near you to vanilla density\n" +
        "  tfit unfit <x> <y> <z> [radius] [dry]\n" +
        "  tfit verbose on|off                - also log skipped placements\n" +
        $"Radius defaults to {DefaultRadius}, max {MaxRadius}, and covers +/-{VerticalReach} blocks vertically.\n" +
        "'dry' reports what would change without changing anything.";

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        if (_params.Count == 0)
        {
            Out(getHelp());
            return;
        }

        switch (_params[0].ToLowerInvariant())
        {
            case "verbose":
                if (_params.Count > 1) TerrainFitConfig.Verbose = _params[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                Out($"Verbose logging is {(TerrainFitConfig.Verbose ? "on" : "off")}.");
                return;
            case "refit":
                Sweep(_params, _senderInfo, unfit: false);
                return;
            case "unfit":
                Sweep(_params, _senderInfo, unfit: true);
                return;
            default:
                Out(getHelp());
                return;
        }
    }

    private static void Sweep(List<string> args, CommandSenderInfo sender, bool unfit)
    {
        var world = GameManager.Instance?.World;
        if (world == null)
        {
            Out("No world loaded.");
            return;
        }

        bool dry = args.Remove("dry");
        Vector3i centre;
        int radius = DefaultRadius;

        if (args.Count >= 4)
        {
            if (!TryInt(args[1], out var x) || !TryInt(args[2], out var y) || !TryInt(args[3], out var z))
            {
                Out("Coordinates must be whole numbers: tfit " + args[0] + " <x> <y> <z> [radius]");
                return;
            }
            centre = new Vector3i(x, y, z);
            if (args.Count >= 5 && !TryInt(args[4], out radius))
            {
                Out("Radius must be a whole number.");
                return;
            }
        }
        else
        {
            var player = SenderPlayer(world, sender);
            if (player == null)
            {
                Out("No player position to work from. From telnet or the web console, give coordinates: tfit " + args[0] + " <x> <y> <z> [radius]");
                return;
            }
            centre = new Vector3i(player.position);
            if (args.Count >= 2 && !TryInt(args[1], out radius))
            {
                Out("Radius must be a whole number.");
                return;
            }
        }

        radius = Math.Max(1, Math.Min(MaxRadius, radius));
        var changes = new List<BlockChangeInfo>();
        var modes = new Dictionary<string, int>();
        int candidates = 0, skippedUnloaded = 0, skippedTrigger = 0;
        var noPending = new HashSet<Vector3i>();

        int yMin = Math.Max(2, centre.y - VerticalReach), yMax = Math.Min(253, centre.y + VerticalReach);
        for (int x = centre.x - radius; x <= centre.x + radius; x++)
        for (int z = centre.z - radius; z <= centre.z + radius; z++)
        {
            var chunk = world.GetChunkFromWorldPos(x, 0, z) as Chunk;
            if (chunk == null)
            {
                skippedUnloaded++;
                continue;
            }
            for (int y = yMin; y <= yMax; y++)
            {
                var bv = world.GetBlock(x, y, z);
                if (!TerrainFit.IsFittableCube(bv, out _)) continue;

                var pos = new Vector3i(x, y, z);
                sbyte density = world.GetDensity(x, y, z);

                if (unfit)
                {
                    // Vanilla never gives a placed cube anything but air density,
                    // so negative (or our +1 wall value) means it was fitted.
                    if (density > 1) continue;
                    candidates++;
                    if (HasTrigger(chunk, pos)) { skippedTrigger++; continue; }
                    changes.Add(Corrections.DensityOnly(pos, MarchingCubes.DensityAir, false));
                    Count(modes, "reset");
                    continue;
                }

                // Already fitted (by us, or baked into a prefab): leave it.
                if (density <= 1) continue;
                candidates++;
                if (HasTrigger(chunk, pos)) { skippedTrigger++; continue; }

                var fit = TerrainFit.Solve(world, pos, noPending);
                if (!fit.Apply) continue;
                changes.Add(Corrections.DensityOnly(pos, fit.Density, fit.Force));
                Count(modes, fit.Mode);
            }
        }

        string verb = unfit ? "reset" : "fit";
        string summary = string.Format(CultureInfo.InvariantCulture,
            "{0} {1} of {2} cubes within {3} of {4}{5}",
            dry ? "Would " + verb : (unfit ? "Reset" : "Fitted"), changes.Count, candidates, radius, centre,
            modes.Count > 0 ? " (" + string.Join(", ", Describe(modes)) + ")" : "");
        if (skippedTrigger > 0) summary += $"; left {skippedTrigger} POI trigger blocks alone";
        if (skippedUnloaded > 0) summary += $"; {skippedUnloaded} columns weren't loaded";

        if (!dry && changes.Count > 0)
            Corrections.ApplyEverywhere(changes);

        Out(summary + ".");
        if (!dry) Log.Out("[KitsuneTerrainFit] " + summary);
    }

    /// <summary>The player running the command: the remote client, or the host in a local game.</summary>
    private static EntityPlayer SenderPlayer(World world, CommandSenderInfo sender)
    {
        if (sender.RemoteClientInfo != null)
            return world.GetEntity(sender.RemoteClientInfo.entityId) as EntityPlayer;
        return sender.IsLocalGame ? world.GetPrimaryPlayer() : null;
    }

    private static bool HasTrigger(Chunk chunk, Vector3i pos) => chunk.GetBlockTrigger(World.toBlock(pos)) != null;

    private static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static void Count(Dictionary<string, int> modes, string mode) =>
        modes[mode] = modes.TryGetValue(mode, out var n) ? n + 1 : 1;

    private static IEnumerable<string> Describe(Dictionary<string, int> modes)
    {
        foreach (var kv in modes) yield return $"{kv.Value} {kv.Key.ToLowerInvariant()}";
    }

    private static void Out(string line) => SdtdConsole.Instance.Output("[KitsuneTerrainFit] " + line);
}
