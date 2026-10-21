# Exterior paint profiles

Heat Tile Painter recognizes retractable wheels, deployable solar panels, and animated docking
ports as deployables. These parts require an explicit `HEAT_TILE_EXTERIOR` model profile. An
unknown deployable stays unpainted and logs one warning per part definition.

The bundled profiles cover stock aircraft landing gear, stock and ReStock shielded 1.25 m docking
ports, and their shrouded 1x6 / 3x2 solar panels. Non-shrouded panels have no paintable cover.
Profiles do not change deployment, power production, docking, wheels, resources, or animations.

```cfg
HEAT_TILE_EXTERIOR
{
    model = MyPack/Parts/Example/model
    renderer = Cover
    renderer = MovingLid
}
```

`model` matches the model path in the renderer hierarchy; renderer names are exact and case
sensitive. Only listed MeshRenderers are considered. The prefab's reference geometry must have
the same topology as the live mesh. The closed shell is used to reject inward-facing triangles,
so shared materials or UVs do not paint mechanisms accidentally. Preview uses the same selection.
Unrecognized topology and shells over 10000 triangles are declined safely.

A validated convex cover around an open aperture can additionally specify `interiorPoint = x,y,z`
in prefab part-local coordinates. This rejects faces pointing toward that point before visibility
testing. Do not add a guessed point to a non-convex model. The stock docking-port profile uses this
to exclude inward-facing faces whose rays would otherwise escape through its docking aperture.

Test a new profile closed, deployed, and retracted, from inside and outside, with flat color and a
pattern. Also test save/load, flight, variants, and clearing paint. Small recessed edges can remain
original because the classifier deliberately requires every sample on a face to be exterior.
