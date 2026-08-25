Heat Tile Painter 0.24.1 by peachyG

Requires Kerbal Space Program 1.12.5 and ModuleManager.

INSTALL

Copy the archive's GameData folder into the Kerbal Space Program folder and merge it with the
existing GameData folder. The mod installs to GameData/HeatTileOverlayPrototype. This older folder
name is kept for saved-craft and update compatibility.

USE

Open Heat Tile Painter from the VAB or SPH toolbar. Choose Select, Eyedropper, Brush, Bucket, or
Eraser. Set the paint, part-local side, coverage, surface wrap, mirror, opacity mask, and edge style.
Hover over the craft to preview the surface, then click to apply it. Press Esc or choose Off to
return to normal editor controls.

Each part keeps an ordered layer stack. Layers can be hidden, copied, updated, deleted, or dragged
into a new order. Select a layer and use Move or Rotate for precise placement. The gizmos follow the
editor's current smooth or snap mode, including angles set by Editor Extensions. Reset returns the
layer to its selected side and centers its coverage plane.

PAINT AND HEAT PROTECTION

The mod includes four heat-tile patterns and Flat Color with RGB controls and presets.

Functional heat protection is optional for each painted part. It raises the internal temperature
limit by 35% and the skin limit by 50%, capped at 3000 K and 3300 K. The tradeoff is 5% more dry
mass. It does not add Ablator or change fuel resources. Remove All turns it off.

RENDERING AND PERFORMANCE

Paint uses mesh-based material regions so models that share UV space do not paint both sides at
once. Repainting starts from the part's current source mesh, including settled procedural geometry.
Flat Color clears inherited specular and metal maps inside its painted region.

One global scheduler handles restoration and repaint work at a limit of one painted part per frame.
The mod adds no persistent overlay renderer, camera callback, collider, physics object, per-part
frame callback, or flight polling. The first use of an uncached texture or region can still pause
the editor while it is baked.

CURRENT LIMITS

- Mask opacity is saved, but the renderer does not interpret it yet.
- Textured gasket and Reinforced strip currently use the same smooth boundary as Soft fade.
- Some complex multi-material parts remain limited by their source UV layout.

Reforged Materials Redux, ReStockPBR, Textures Unlimited, Shabby, Resurfaced, Technicolor, and
Conformal Decals are not required.

Heat Tile Painter is released under the MIT License. See LICENSE.txt. Reforged and B9 Procedural
Wings attribution is in THIRD-PARTY-NOTICES.txt and LICENSE-ASSETS.txt.
