# KitsuneTerrainFit

🦊 **Part of the [Kitsune Systems Collection](https://github.com/Kitsune-Den)**

Put a block down on the ground in 7 Days to Die and look at its base. The dirt dips away from it in a little trench, like the block repels the ground. Every wall, every foundation, every garden bed.

This makes the ground meet the block flush.

## Install

1. Drop the `KitsuneTerrainFit` folder into `<7D2D>/Mods/`
2. Restart the server. You should see `[KitsuneTerrainFit] Loaded` in the log

**Server-side only.** Players don't install anything. The fitted density travels to them in the game's normal block-change packets.

## What it does

7D2D draws terrain with marching cubes over a density value per voxel: negative is solid ground, positive is air. A block you place keeps the air density of the space it went into, so the terrain mesh treats it as a hole and slopes down into it. That's the trench.

When you place a **full, opaque cube** (or a **farm plot**) next to the ground, this gives the cube a density that puts the terrain surface at the same height as the ground around it. The terrain then runs flat right up to the cube, and the bit of surface that ends up inside the cube is hidden by the cube.

| Situation | What it does |
|---|---|
| Ground beside the cube at the same level | Matches the cube to the surrounding ground height, never higher than the lowest neighbour by more than a hair (so no lip pokes out) |
| Cube on a slope | `hillside="embed"` (default): fitted to the ground around it, so the hill washes over the uphill edge and it looks sunk in. `hillside="clear"`: fills only as much as keeps the ground under the top edge |
| Cube sitting just above lower ground | Raises the ground under its edges to meet it. Closer ground gets more fill |
| Ground only diagonal to the cube | A small, capped fill |
| Cube built into a cut in a hillside | `embed`: pulls the hill in over the edge. `clear`: leaves it vanilla, since air density already puts the hillside flush against the face |
| A floor, step, slab, door or other partial block next to it (sides or diagonals) | Leaves it vanilla. The fill shapes ground into the neighbouring spaces, and a partial block can't hide it, so dirt would poke up through the floor |
| Water next to the cube, ground or another fitted block on top of it, not a full cube, glass, anything with a tile entity, multi-blocks | Leaves it vanilla |

Breaking the block puts the voxel back to air the normal vanilla way. Upgrading or repairing it keeps the fitted density. Placing a floor, step or other partial block next to a fitted cube puts that cube back to vanilla, so building walls before floors doesn't leave dirt along the seams.

### Fixing what's already built

Cubes placed before you installed this still have the trench. Admins can fit them in place:

```
tfit refit              fit cubes within 16 blocks of you
tfit refit 32 dry       just report what would change within 32
tfit refit x y z 24     around a point (from telnet or the web console)
tfit unfit 16           put cubes near you back to vanilla density
tfit verbose on         also log every placement it skipped, and why
```

`refit` only touches cubes that still have vanilla density, and leaves POI trigger blocks alone. `unfit` resets any full cube with solid density, which includes blocks a prefab shipped that way, so run it with `dry` first if you're near a POI.

## Settings

`Settings.xml` in the mod folder:

```xml
<KitsuneTerrainFit enabled="true" alsoFit="farmPlotBlock*" alsoFitHeight="0.75" hillside="embed" verbose="false" />
```

`enabled="false"` makes placement fully vanilla. `tfit` still works.

`alsoFit` lists blocks to fit even though they aren't six full faces, as long as their walls hide the ground tucked inside. Farm plots qualify: an open soil top and sides that cull against neighbours, but solid walls all the way round. Comma separated, `*` matches anything, `alsoFit=""` turns it off.

With `hillside="clear"`, and always for farm plots, there's also a top-edge limit. The terrain mesh joins neighbouring columns with straight slopes, so at a block's face the ground sits halfway between the neighbour's height and the block's. The fill is capped so that halfway point never rises above the block's top edge, otherwise higher ground next door spills over and buries the edge.

`alsoFitHeight` caps how far up those blocks the ground may come, as a fraction of their height. A farm plot's soil sits a little below its rim, so fitting it like a cube (all the way to the top) buries the soil. At the default 0.75 the ground stays just under the soil and still meets the rim with only a slight bevel. Open-topped blocks dug into a hillside are left vanilla, because vanilla already puts the wall flush against them and a fill would pull the hill in over the rim.

## How it hooks in

- **Prefix on `NetPackageSetBlock.ProcessPackage`** (player placements) and **`GameManager.SetBlocksRPC`** (server-made placements): adjusts the density on the change before the server applies it and relays it.
- **Correction to the placing player.** The server relays a player's placement to everyone *except* that player, because their client already applied it. Without this, the person who placed the block wouldn't see the fit until the chunk reloaded. We send them a small density-only change straight after.
- **Paint is preserved.** A density-only change makes vanilla reset the voxel's paint, so every change we send also carries the block's current paint.
- Only one log line per fitted block (block name, position, who placed it, what it did). Skips are logged only when `verbose` is on.

## Credits

The approach of setting a placed cube's density to match the surrounding ground comes from **GenesisFill** by GenesisMods. KitsuneTerrainFit is a separate implementation. It adds the client correction, paint preservation, refitting existing builds, and quieter logs.

## License

MIT
