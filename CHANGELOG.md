# Changelog

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
