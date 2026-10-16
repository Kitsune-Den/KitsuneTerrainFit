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
| Ground beside the cube at the same level | Matches the cube to the surrounding ground height, never higher than the lowest neighbour by more than a hair (so no lip pokes out), and always a few centimetres under the cube's top so you can still aim at it |
| Cube on a slope | `hillside="embed"` (default): fitted to the ground around it, so the hill washes over the uphill edge and it looks sunk in. `hillside="clear"`: fills only as much as keeps the ground under the top edge |
| Cube sitting just above lower ground | Raises the ground under its edges to meet it. Closer ground gets more fill |
| Ground only diagonal to the cube | A small, capped fill |
| Cube built into a cut in a hillside | `embed`: pulls the hill in over the edge. `clear`: leaves it vanilla, since air density already puts the hillside flush against the face |
| A rounded corner, pillar or other upright shape next to it | Only the level fill; otherwise keeps the natural ground density (see below) |
| A floor, step, slab, door or other partial block next to it (sides or diagonals) | Leaves it vanilla. The fill shapes ground into the neighbouring spaces, and a partial block can't hide it, so dirt would poke up through the floor |
| Water next to the cube, ground or another fitted block on top of it, not a full cube, glass, anything with a tile entity, multi-blocks | Leaves it vanilla |

### Rounded corners, pillars and other upright shapes

A fill puts ground inside the block's space up to the fitted height. A cube hides that, but a rounded corner or pillar has open parts where it shows. So these shapes only get a fill that keeps the ground level:

- **Level ground** (ground beside the block at its own level): the same fill a cube gets. It never rises above the lowest ground around, so in the open parts it just looks like the ground carrying on.
- **Anything else** (resting above lower ground, only touching diagonally, against a hill cut): the mod keeps the density the space already had. Vanilla resets the air just above natural ground to full air when you place a block, and that's what pulls the ground down into the trench. Keeping it leaves the ground exactly where it was. This only helps where the ground was still natural. A spot that was dug out, or where a block was picked up, is already full air, so there's nothing to keep.

"Upright" is read from the shape's mesh at its current rotation: every surface has to be a vertical wall or flat on the block's bottom or top. That covers rounded corners, round and square pillars, quarter rounds and poles, standing upright or upside down. Steps, ramps, plates, half blocks and shapes lying on their side stay vanilla, because ground would poke through them.

Cubes next to an upright shape follow the same rule: the level fill, or keep what's there. Putting an upright shape next to a fitted cube leaves a level fit alone and drops anything higher to natural ground. `tfit refit` gives upright shapes the level fill too, and `tfit unfit` resets them. `shapes="vanilla"` turns all of this off.

Breaking the block puts the voxel back to air the normal vanilla way. Upgrading or repairing it keeps the fitted density. Placing a floor, step or other partial block next to a fitted cube puts that cube back to vanilla, so building walls before floors doesn't leave dirt along the seams.

### Fixing what's already built

Cubes placed before you installed this still have the trench. Admins can fit them in place:

```
tfit refit              fit cubes within 16 blocks of you
tfit refit 32 dry       just report what would change within 32
tfit refit x y z 24     around a point (from telnet or the web console)
tfit unfit 16           put cubes near you back to vanilla density
tfit lower 16           blocks you can't aim at because the ground covers their top
tfit verbose on         also log every placement it skipped, and why
```

The fill always stops a few centimetres under a block's top face. The game decides what you're aiming at with a physics ray, and if the ground mesh sits on or over a block's top, the ray hits the ground first and you end up aiming at the dirt underneath: no pick-up prompt, and the hammer does nothing. Blocks fitted by v0.1.6 or earlier can be flush like that, mostly ones placed in a hole. `tfit lower` brings their ground back down. It's a separate command because a prefab can ship blocks flush with the ground on purpose, so run it with `dry` first near a POI.

`refit` only touches cubes that still have vanilla density, and leaves POI trigger blocks alone. `unfit` resets any full cube with solid density, which includes blocks a prefab shipped that way, so run it with `dry` first if you're near a POI.

## Settings

`Settings.xml` in the mod folder:

```xml
<KitsuneTerrainFit enabled="true" alsoFit="farmPlotBlock*" alsoFitHeight="0.75" hillside="embed" shapes="keep" verbose="false" />
```

`shapes="keep"` (default) keeps natural ground around upright shapes like rounded corners and pillars. `shapes="vanilla"` leaves them alone.

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
