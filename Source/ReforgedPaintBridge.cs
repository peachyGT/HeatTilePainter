using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using HeatTileOverlayPrototype.ReforgedCore;

namespace HeatTileOverlayPrototype
{
    public sealed partial class ModuleHeatTileOverlay
    {
        private bool _reportedMissingReforgedCore;
        private bool _suppressVariantRefresh;
        private int _suppressVariantRefreshThroughFrame = -1;

        private bool SuppressVariantRefresh
        {
            get { return _suppressVariantRefresh || Time.frameCount <= _suppressVariantRefreshThroughFrame; }
        }

        private ModuleHeatTileReforgedCore ReforgedCore
        {
            get
            {
                return part == null ? null
                    : part.FindModuleImplementing<ModuleHeatTileReforgedCore>();
            }
        }

        private bool TryRebuildReforgedPaint()
        {
            ModuleHeatTileReforgedCore core = ReforgedCore;
            if (core == null)
            {
                if (!_reportedMissingReforgedCore)
                {
                    _reportedMissingReforgedCore = true;
                    Debug.LogError("[HeatTilePainter] Reforged core module is missing on "
                        + (part != null && part.partInfo != null ? part.partInfo.name : "unknown part")
                        + "; paint was not applied.");
                }
                return false;
            }

            List<string> zones = new List<string>();
            for (int i = 0; i < _layers.Count; i++)
            {
                HeatTileLayerState layer = _layers[i];
                if (layer == null || !layer.Visible || layer.Coverage <= 0.001f
                    || layer.Opacity <= 0.001f) continue;

                AddReforgedZone(zones, layer, CoverageDirection(layer));
                if (layer.MirrorMode == 1)
                    AddReforgedZone(zones, layer, MirroredDirection(layer));
            }

            core.ApplyExternalZones(string.Join("^", zones.ToArray()));
            Debug.Log("[HeatTilePainter] Reforged core applied " + zones.Count + " region(s) on "
                + (part.partInfo != null ? part.partInfo.name : part.name) + ".");
            return true;
        }

        private static void AddReforgedZone(List<string> zones, HeatTileLayerState layer,
            Vector3 direction)
        {
            HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
            string texture = definition.IsFlatColor
                ? "tint#" + HexColor(layer.Tint)
                : TextureLeaf(definition.TexturePath);
            int opacity = Mathf.Clamp(Mathf.RoundToInt(layer.Opacity), 1, 100);
            string stack = texture + ":whole:" + opacity.ToString(CultureInfo.InvariantCulture);

            float coverage = Mathf.Clamp(layer.Coverage, 0f, 100f);
            // Values 1000..2000 are this fork's versioned tenths-of-a-percent encoding. The
            // Reforged parser still accepts upstream/older 0..100 tokens, while move-gizmo edits
            // are no longer quantized to coarse whole-percent jumps on large parts.
            int cut = 1000 + Mathf.Clamp(Mathf.RoundToInt(EffectiveCutPercent(layer) * 10f), 0, 1000);
            // Hard means an exact geometric boundary. Reforged owns the resulting disposable
            // triangle partition and always rebuilds it from the pristine/source mesh. This is
            // essential on KSP parts whose top and bottom faces reuse the same UV pixels.
            float fadeOfPaintedDepth = layer.EdgeStyle == 0
                ? 0f : (layer.EdgeBlend / Mathf.Max(coverage, 0.1f) * 100f);
            int fade = Mathf.Clamp(Mathf.RoundToInt(fadeOfPaintedDepth), 0, 40);
            string region = VectorRegion(direction, cut, fade, layer.DirectionFacingOnly,
                layer.SurfaceWrap);

            // Reforged zone format:
            // region~finish~metal~smooth~colored~r~g~b~shineSpot~scale~weatherStack
            zones.Add(region + "~0~0~0~0~1~1~1~0~1~" + stack);
        }

        private static string VectorRegion(Vector3 direction, int cut, int fade,
            bool directionFacingOnly, float surfaceWrap)
        {
            if (direction.sqrMagnitude < 0.000001f) direction = Vector3.down;
            direction.Normalize();
            int x = Mathf.RoundToInt(direction.x * 1000f);
            int y = Mathf.RoundToInt(direction.y * 1000f);
            int z = Mathf.RoundToInt(direction.z * 1000f);
            return "v" + x.ToString(CultureInfo.InvariantCulture) + "_"
                + y.ToString(CultureInfo.InvariantCulture) + "_"
                + z.ToString(CultureInfo.InvariantCulture) + "_"
                + cut.ToString(CultureInfo.InvariantCulture) + "_"
                + fade.ToString(CultureInfo.InvariantCulture) + "_100"
                + "_" + (directionFacingOnly ? "1" : "0") + "_"
                + Mathf.Clamp(Mathf.RoundToInt(surfaceWrap), 0, 180)
                    .ToString(CultureInfo.InvariantCulture);
        }

        private static string TextureLeaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "hrsi_legacy";
            int slash = path.LastIndexOf('/');
            return slash >= 0 && slash + 1 < path.Length ? path.Substring(slash + 1) : path;
        }

        private static string HexColor(Color color)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:X2}{1:X2}{2:X2}",
                Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255),
                Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255),
                Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255));
        }

        private void ClearReforgedPaint(bool reapplyVariant = false)
        {
            ModuleHeatTileReforgedCore core = ReforgedCore;
            if (core == null) return;
            _suppressVariantRefresh = true;
            _suppressVariantRefreshThroughFrame = Time.frameCount + 2;
            try { core.ClearAll(reapplyVariant); }
            finally { _suppressVariantRefresh = false; }
        }
    }
}
