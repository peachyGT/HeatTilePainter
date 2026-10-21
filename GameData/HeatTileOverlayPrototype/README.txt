Heat Tile Painter 0.25.0 by peachyG

Requires Kerbal Space Program 1.12.5 and ModuleManager.

INSTALL

Copy the archive's GameData folder into the Kerbal Space Program folder and merge it with the
existing GameData folder. The mod installs to GameData/HeatTileOverlayPrototype. This older folder
name is kept for saved-craft and update compatibility.

USE

Open Heat Tile Painter from the VAB or SPH toolbar. Choose Select, Eyedropper, Brush, Bucket, or
Eraser. Set the paint, part-local side, coverage, surface wrap, mirror, opacity, and edge style.
Hover over the craft to preview the surface, then click to apply it. Press Esc or choose Off to
return to normal editor controls.

Textured paint has a per-layer Pattern size control. Browse / Add custom opens an in-game image
browser for PNG and JPG files. Selected files are copied into PluginData/UserTextures, so the
paint does not depend on the original file remaining in place.

Each part keeps an ordered layer stack. Layers can be hidden, copied, updated, deleted, or dragged
into a new order. Select a layer and use Move or Rotate for precise placement. The gizmos follow the
editor's current smooth or snap mode, including angles set by Editor Extensions. Reset returns the
layer to its selected side and centers its coverage plane.

PAINT AND HEAT PROTECTION

The mod includes four heat-tile patterns and Flat Color with RGB controls and presets.

Texture packs can add HEAT_TILE_TEXTURE config nodes without compiling a plugin. See
CustomTextures.cfg.example and PluginData/README-Custom-Textures.txt.

Functional heat protection is optional. Less/More selects 0.25x-4x coating at +1.25%-20% dry mass.
The panel shows the actual limits. Existing enabled craft retain 1x: +35% internal / +50% skin
limits, capped at 3000/3300 K, for +5% dry mass. At 4x, multipliers reach 2.4/3 and caps reach
6000/6600 K. Low-temperature parts may remain below the caps. Original limits are never lowered.
Protection does not add Ablator or change fuel resources. Remove All turns it off.

Stock retractable aircraft gear and stock/ReStock shielded docking ports and shrouded 1x6/3x2
solar panels paint only their external covers. Mechanisms and solar cells remain original.
Unrecognized deployable models stay unpainted. ExteriorProfiles.cfg contains the model profiles.

RENDERING AND PERFORMANCE

Paint uses mesh-based material regions so models that share UV space do not paint both sides at
once. Repainting starts from the part's current source mesh, including settled procedural geometry.
Textured patterns use settled part-local coordinates during the bake, so stock UV islands do not
set their physical size or phase.
Flat Color clears inherited specular and metal maps inside its painted region.

One global scheduler handles restoration and repaint work at a limit of one painted part per frame.
The mod adds no persistent overlay renderer, camera callback, collider, physics object, per-part
frame callback, or flight polling. The first use of an uncached texture or region can still pause
the editor while it is baked.

CURRENT LIMITS

- The saved legacy Mask opacity switch is disabled; placed layers always use their region mask.
- Gasket (fade) and Reinforced (fade) use a soft boundary.
- Some complex multi-material parts use a topology-preserving UV mask, so overlapping UV islands
  can still limit region-boundary precision.
- Overlapping unit-atlas triangles use additional material batches, increasing first-bake cost.
  Zero-area UV faces cannot carry a spatial pattern and may show a smear.
  Repeating UVs, regions over 4000 triangles, and more than 16 overlapping batches retain the
  bounded compatibility path and can still show mapping artifacts.

Reforged Materials Redux, ReStockPBR, Textures Unlimited, Shabby, Resurfaced, Technicolor, and
Conformal Decals are not required.

Heat Tile Painter is released under the MIT License. See LICENSE.txt. Reforged and B9 Procedural
Wings attribution is in THIRD-PARTY-NOTICES.txt and LICENSE-ASSETS.txt.
