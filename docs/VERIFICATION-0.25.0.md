# 0.25.0 verification

Accepted DLL SHA-256: `C192218A0C5D05D65ECFB7F1C32440CD78613DD7DF1C62E52089AE3EB6E11074`.
The installed DLL and release DLL were matched before acceptance on 2026-10-20.

The deterministic quality gate checks backend ownership and event contracts, thermal arithmetic,
opacity, cache identity, finite values, import headers, UV partitioning, UI contracts, and persistence.
It includes 53 thermal assertions and 101 opacity/finite-value assertions. These checks alone do
not verify visible paint.

Separately, a private harness exercised the actual KSP 1.12.5 editor and flight scenes in an isolated
save, with ReStock, ProceduralParts, KSP Community Fixes, and Editor Extensions installed. Real KSP
screenshots were inspected. The harness used game methods and UI handlers; this was not manual
keyboard/mouse testing. Private test code and game assemblies are not distributed.

| Runtime case | Result |
| --- | --- |
| Retained nose and circular-intake scaling regressions | Checker and HRSI screenshots inspected; independent baked-pixel samples passed 100/100 and 246/246 valid non-edge samples. |
| Procedural nose resize | 0.625 m to 1.5 m followed the generator mesh; 514/514 valid samples passed. Clearing restored the current generator mesh, not the original size. |
| Thermal amount and ownership | Minimum/default/maximum, dry-mass formula, high original limits, external writers, disabling, and unchanged resources passed on representative nose/intake, gear/dock, and Mk2 tank parts. |
| Lifecycle | Save/load, live module reload, launch/revert, variant changes, and removal passed. |
| Undo and symmetry | Applied KSPCF undo and controlled stock RestoreState timing passed. Thermal propagation passed on a separate live symmetry counterpart fixture. |
| Deployable covers | Stock landing gear and stock/ReStock shielded docking and solar covers inspected closed, deployed, and retracted; mechanisms and functional surfaces stayed original. Representative HRSI covers survived save/load and flight/revert. |
| Unsupported deployable | Unshrouded solar panel retained exact original mesh/material identities. |
| Preview and editor controls | Gear exterior preview, move/rotate gizmos, snap, clipboard order, and 24 conditional UI state changes passed. |
| Custom textures | PNG/JPEG imports, source-file removal, restart resolution, missing-ID round-trip, malformed and oversized image rejection passed. |
| Unity pixel fixtures | Low opacity, mask differences, normal-map identity, and precise scale cache identity passed. |

Zero-area UV triangles were explicitly excluded from spatial-pattern sample counts. They cannot
encode a spatial pattern and can still smear. Repeated UVs and large or complex meshes retain the
documented bounded fallback. Conservative exterior selection can leave recessed edges unpainted.

On the tested modded installation, first HRSI bakes took about 2625 ms for the nose and 658 ms for
the intake; warm repaints took about 10.6 ms and 4.3 ms. Warm synchronous craft-load handlers took
about 66 ms, excluding queued paint settling. These are fixture measurements, not general performance
guarantees. Renderer mesh/material identities stayed unchanged during an idle interval over 60 seconds.

Fresh logs were reviewed for painter exceptions and GUILayout errors. The modded installation had
unrelated stock UI/mod exceptions, and a malformed private solar animation fixture was corrected and
retested. No atmospheric reentry trial or universal compatibility claim is made.
