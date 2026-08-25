Forum title: `[1.12.5] Heat Tile Painter 0.24.1`

Forum post:

I wanted a way to paint heat tiles onto the parts I was already using, without covering a craft in tens of
decal parts or losing a large chunk of the frame rate. Heat Tile Painter is where that ended up.

It runs in the VAB and SPH. You can paint four heat-tile textures or a flat color onto a chosen side
of a part, stack several layers, move or rotate them with KSP-style gizmos, and adjust the coverage,
edge, surface wrap, mirror, and layer order. Paint stays with the craft through save and load,
launch, and revert.

This was about 70% vibe-coded. This did take a lot of iteration and manual rewriting, but overall it's a pretty light and simple project that didn't take too much effort.

![Heat Tile Painter flight topside](https://raw.githubusercontent.com/peachyGT/HeatTilePainter/main/docs/screenshots/flight-topside.png)

![Heat Tile Painter tiled underside](https://raw.githubusercontent.com/peachyGT/HeatTilePainter/main/docs/screenshots/flight-underside.png)

![Heat Tile Painter upper atmosphere](https://raw.githubusercontent.com/peachyGT/HeatTilePainter/main/docs/screenshots/upper-atmosphere-underside.png)

![Heat Tile Painter editor UI](https://raw.githubusercontent.com/peachyGT/HeatTilePainter/main/docs/screenshots/editor-ui.png)

## Download and source

- Download: [https://github.com/peachyGT/HeatTilePainter/releases/latest](https://github.com/peachyGT/HeatTilePainter/releases/latest)
- Source and issue tracker: [https://github.com/peachyGT/HeatTilePainter](https://github.com/peachyGT/HeatTilePainter)
- CKAN: submitting now

## Requirements

- Kerbal Space Program 1.12.5
- ModuleManager

Reforged Materials Redux is not required. ReStockPBR, Textures Unlimited, Shabby, Resurfaced,
Technicolor, and Conformal Decals are also not required.

## Install

Copy the downloaded archive's `GameData` folder into the Kerbal Space Program folder and merge it
with the existing `GameData` folder. The installed path is `GameData/HeatTileOverlayPrototype`.

## Use

1. Open Heat Tile Painter from the VAB or SPH toolbar.
2. Choose Select, Eyedropper, Brush, Bucket, or Eraser.
3. Pick a tile pattern or Flat Color. Set the part-local side, coverage, surface wrap, mirror,
   opacity mask, and edge style.
4. Hover over the craft to preview the painted surface, then click to apply it.
5. Select a saved layer and use Move or Rotate for fine placement. The gizmos follow KSP's smooth
   or snap setting, including angles set by Editor Extensions.
6. Press `Esc` or choose Off to return to normal editor controls.

Functional heat protection is optional for each painted part. It raises the internal temperature
limit by 35% and the skin limit by 50%, capped at 3000 K and 3300 K. The tradeoff is 5% more dry
mass. It does not add Ablator or change fuel resources.

## Current limits

- Mask opacity is saved, but the renderer does not interpret it yet.
- Textured gasket and Reinforced strip currently use the same smooth boundary as Soft fade.
- Some complex multi-material parts remain limited by their source UV layout.
- The first use of an uncached texture or region can pause the editor while it is baked.

## License and credits

Heat Tile Painter is released under the MIT License by peachyG.

The rendering and cache core is based on Reforged Materials Redux by J4COB, used under MIT. The
HRSI patterns include MIT-licensed artwork derived from B9 Procedural Wings. Reforged textures are
not included. Stock KSP Move and Rotate icons are read from the running game and are not
redistributed. The download and source repository contain the full notices.

## Bug reports

Please include `KSP.log`, the craft file when you can, KSP and ModuleManager versions, steps to
reproduce the problem, and a list of installed visual or editor mods.
