# Changelog

## 0.25.0 - 2026-10-20

- Added adjustable optional coating from 0.25x to 4x, with +1.25% to +20% dry mass and
  temperature caps reaching 6000 K internal / 6600 K skin. Existing enabled craft retain 1x.
- Preserved higher original temperature limits and later changes from other modules when
  applying or removing protection. Reloading does not compound protection.
- Added exterior-only profiles for stock aircraft landing gear and stock/ReStock shielded
  docking ports and shrouded solar panels. Covers retain paint while mechanisms stay original.
- Separated overlapping atlas triangles in painted regions to repair the circular-intake
  projection regression. Preserved original live meshes when clearing paint.
- Applied the surface-wrap cutoff to hard-edge geometry as well as preview.
- Kept procedural resizing attached to the generator's original mesh when other modules clone
  painted meshes. Fixed live state reloads and undo timing for stock KSP and KSP Community Fixes.
- Fixed low-opacity interpretation, source-normal/mask/scale cache identity, teardown cleanup,
  and non-finite numeric input. Import dimensions are checked before image decoding.
- Clarified the disabled legacy opacity-mask switch and the two fade-only edge styles.

- Projected Heat Tile Painter patterns from settled part-local geometry instead of using each
  stock UV atlas as the pattern coordinate field. A layer now keeps one physical scale and phase
  across renderer/material boundaries, including UV-distorted noses and circular intakes.
- Kept the material-specific U/V estimator as the compatibility fallback for legacy Reforged
  weather textures that do not carry a Heat Tile Painter surface projection.
- Made each texture definition's `patternSpan` value authoritative in the active renderer.
- Added a per-layer Pattern size control from 0.25x to 4x, saved with the craft.
- Added an in-game browser for PNG and JPG paint. Imports are copied into a persistent local user
  library and become selectable without restarting KSP.
- Made arbitrary `HEAT_TILE_TEXTURE` GameData paths and optional normal maps work in the active
  renderer, with deterministic last-definition-wins overrides.
- Preserved missing texture IDs instead of silently replacing them with the first built-in paint.
- Fixed the editor GUILayout exception triggered by switching conditional paint, mirror, wrap, or
  edge controls between Layout and Repaint.
- Added a documented, copyable texture-pack format and extension example.

## 0.24.1 - 2026-08-25

First public release.

- Added ordered paint layers to individual parts, with four heat-tile patterns and Flat Color.
- Added controls for part-local side, coverage, surface wrap, mirror, opacity mask, and edge style.
- Added Move and Rotate gizmos for layer placement. They follow KSP's smooth or snap setting and
  angles supplied by Editor Extensions.
- Added layer and stack copying, drag reordering, visibility, symmetry painting, and linked Bucket
  layers.
- Kept paint through craft save and load, launch, revert, variant changes, and supported procedural
  geometry changes.
- Moved paint to a Reforged-based material-region renderer to stop top and bottom surfaces from
  sharing paint.
- Removed persistent overlay renderers and per-part frame polling.
- Added optional heat protection with 35% higher internal and 50% higher skin temperature limits,
  capped at 3000 K and 3300 K, for 5% more dry mass.
- Reworked the editor window around KSP-style controls and stock Move and Rotate icons.
- Limited saved-craft restoration and repaint to one painted part per frame under one global
  scheduler.

The README lists the current limits.
