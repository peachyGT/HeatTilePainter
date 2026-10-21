# Adding custom textures

There are two ways to add paint to Heat Tile Painter.

## Add one image in the editor

Choose **Browse / Add custom** in the Paint section. The browser accepts PNG, JPG, and JPEG files.
It starts in GameData and includes a shortcut to Pictures, so it can reach KSP flags and decal
artwork as well as images elsewhere on the computer.

Imports are limited to 32 MB and 4096 x 4096 pixels to keep editor memory and first-bake time
bounded.

Heat Tile Painter copies the image into:

```text
GameData/HeatTileOverlayPrototype/PluginData/UserTextures
```

The original file is no longer needed after import. Craft files save a generated texture ID, not
the original PC path. The generated library is local user data and is not included when the mod is
packaged from source.

Use the layer's **Pattern size** control after import. A smaller value repeats the image more often;
a larger value makes each repeat larger.

The browser imports color images only. Texture packs can add a normal map with the config format
below.

## Ship a texture pack

Place the texture in your own folder under GameData and add a `HEAT_TILE_TEXTURE` node. No plugin
or patch to Heat Tile Painter is required.

```cfg
HEAT_TILE_TEXTURE
{
    id = my_pack_black_tiles
    displayName = My Pack Black Tiles
    texture = MyTexturePack/Textures/black_tiles
    normal = MyTexturePack/Textures/black_tiles_n
    patternSpan = 1.28
    specularity = 0.04
    shininess = 0.06
}
```

Texture paths are relative to GameData and omit the file extension. PNG and DDS are the safest
choices for a distributed KSP texture pack; any format loaded by KSP's GameDatabase can be used.
The `normal` line is optional.

### Fields

| Field | Required | Meaning |
|---|---:|---|
| `id` | yes | Stable ID saved in craft files. Use letters, numbers, `_`, `-`, or `.`, add a pack-specific prefix, and do not change it after release. |
| `displayName` | no | Text shown in the Paint list. Defaults to `id`. |
| `texture` | yes for images | GameData texture path without an extension. |
| `normal` | no | GameData normal-map path without an extension. |
| `patternSpan` | no | Physical width in metres represented by one full image repeat. Default: `1.28`. Image aspect ratio determines the physical height. |
| `specularity` | no | Specular strength from 0 to 1. Default: `0.06`. |
| `shininess` | no | Gloss/shininess from 0 to 1. Default: `0.08`. |
| `hidden` | no | Set `true` to keep a supporting definition out of the Paint list. |
| `flatColor` | no | Set `true` for a color-only entry instead of an image. |
| `defaultColor` | no | RGBA values from 0 to 1 for a flat-color entry. |

`patternSpan` is the baseline. A saved layer at 2x uses twice that span; 0.5x uses half of it.

ModuleManager supplies the final config database. If more than one valid node has the same `id`,
the last node wins and Heat Tile Painter writes one warning to `KSP.log`. That allows ordinary
ModuleManager ordering and patches to override a definition deliberately.

If a craft references an ID that is not installed, Heat Tile Painter keeps the ID and labels the
layer `Missing`. Reinstalling the texture pack restores the paint without editing the craft file.

See `GameData/HeatTileOverlayPrototype/CustomTextures.cfg.example` for the same minimal example in
the installed mod.
