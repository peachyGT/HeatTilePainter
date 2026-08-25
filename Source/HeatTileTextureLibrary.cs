using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace HeatTileOverlayPrototype
{
    internal sealed class HeatTileTextureDefinition
    {
        public string Id;
        public string DisplayName;
        public string TexturePath;
        public string NormalPath;
        public bool IsFlatColor;
        public bool Hidden;
        public Color DefaultColor;
        public float PatternSpan;
        public float Specularity;
        public float Shininess;
    }

    internal static class HeatTileTextureLibrary
    {
        private static readonly List<HeatTileTextureDefinition> DefinitionsInternal = new List<HeatTileTextureDefinition>();
        private static readonly List<HeatTileTextureDefinition> PaintDefinitionsInternal = new List<HeatTileTextureDefinition>();
        private static bool _loaded;

        public static IList<HeatTileTextureDefinition> Definitions
        {
            get
            {
                EnsureLoaded();
                return PaintDefinitionsInternal;
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            DefinitionsInternal.Clear();
            PaintDefinitionsInternal.Clear();

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("HEAT_TILE_TEXTURE");
            for (int i = 0; i < nodes.Length; i++)
            {
                ConfigNode node = nodes[i];
                string id = node.GetValue("id");
                string displayName = node.GetValue("displayName");
                string texture = node.GetValue("texture");
                bool isFlatColor = ParseBool(node.GetValue("flatColor"), false);
                if (string.IsNullOrEmpty(id) || (!isFlatColor && string.IsNullOrEmpty(texture))) continue;

                HeatTileTextureDefinition definition = new HeatTileTextureDefinition();
                definition.Id = id;
                definition.DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
                definition.TexturePath = texture;
                definition.NormalPath = node.GetValue("normal");
                definition.IsFlatColor = isFlatColor;
                definition.Hidden = ParseBool(node.GetValue("hidden"), false);
                definition.DefaultColor = ParseColor(node.GetValue("defaultColor"), new Color(0.165f, 0.165f, 0.165f, 1f));
                definition.PatternSpan = ParseFloat(node.GetValue("patternSpan"), 1.28f);
                definition.Specularity = ParseFloat(node.GetValue("specularity"), 0.06f);
                definition.Shininess = ParseFloat(node.GetValue("shininess"), 0.08f);
                DefinitionsInternal.Add(definition);
                if (!definition.Hidden) PaintDefinitionsInternal.Add(definition);
            }

            if (DefinitionsInternal.Count == 0)
            {
                HeatTileTextureDefinition fallback = new HeatTileTextureDefinition
                {
                    Id = "legacy_hrsi",
                    DisplayName = "B9 Legacy HRSI Panels",
                    TexturePath = "HeatTileOverlayPrototype/Assets/hrsi_legacy",
                    NormalPath = "HeatTileOverlayPrototype/Assets/hrsi_legacy_n",
                    IsFlatColor = false,
                    DefaultColor = Color.white,
                    PatternSpan = 1.28f,
                    Specularity = 0.06f,
                    Shininess = 0.08f
                };
                DefinitionsInternal.Add(fallback);
                PaintDefinitionsInternal.Add(fallback);
            }
            else if (PaintDefinitionsInternal.Count == 0) PaintDefinitionsInternal.Add(DefinitionsInternal[0]);
        }

        public static HeatTileTextureDefinition Find(string id)
        {
            EnsureLoaded();
            for (int i = 0; i < DefinitionsInternal.Count; i++)
            {
                if (string.Equals(DefinitionsInternal[i].Id, id, StringComparison.OrdinalIgnoreCase)) return DefinitionsInternal[i];
            }
            return DefinitionsInternal[0];
        }

        public static int FindIndex(string id)
        {
            EnsureLoaded();
            for (int i = 0; i < PaintDefinitionsInternal.Count; i++)
            {
                if (string.Equals(PaintDefinitionsInternal[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

#if false // Retired in 0.19 with the persistent decal/transparent overlay backend.
        public static Material CreateMaterial(string id, float opacity, Color tint)
        {
            HeatTileTextureDefinition definition = Find(id);
            opacity = Mathf.Clamp01(opacity);
            bool hasNormal = !string.IsNullOrEmpty(definition.NormalPath);
            Shader shader = null;
            ResolveDeferredDecalShader();
            shader = _decalShader;
            // Compatibility path for installations without Conformal Decals/Shabby. The normal
            // runtime path uses Conformal Decals' purpose-built deferred shader: it depth-biases
            // against the source surface without a camera-relative transform or transparent
            // forward-lighting pass.
            if (shader == null && hasNormal) shader = Shader.Find("Legacy Shaders/Transparent/Bumped Specular");
            if (shader == null) shader = Shader.Find("KSP/Alpha/Translucent Specular");
            if (shader == null) shader = Shader.Find("KSP/Alpha/Translucent");
            if (shader == null) shader = Shader.Find("KSP/Specular (Transparent)");
            if (shader == null) shader = Shader.Find("KSP/Alpha/Unlit Transparent");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            if (shader == null) throw new InvalidOperationException("No transparent overlay shader is available");

            Material material = new Material(shader);
            material.name = "HeatTilePainter-" + definition.Id;
            bool deferredDecal = IsDeferredDecalMaterial(material);
            Texture2D texture = definition.IsFlatColor
                ? Texture2D.whiteTexture
                : GameDatabase.Instance.GetTexture(definition.TexturePath, false);
            if (texture != null)
            {
                texture.wrapMode = TextureWrapMode.Repeat;
                texture.filterMode = FilterMode.Bilinear;
                string colorProperty = ColorTextureProperty(material);
                if (material.HasProperty(colorProperty)) material.SetTexture(colorProperty, texture);
                else material.mainTexture = texture;
            }
            else
            {
                Debug.LogError("[HeatTilePainter] Missing texture: " + definition.TexturePath);
            }

            if (hasNormal)
            {
                Texture2D normal = GameDatabase.Instance.GetTexture(definition.NormalPath, true);
                if (normal != null && material.HasProperty("_BumpMap"))
                {
                    normal.wrapMode = TextureWrapMode.Repeat;
                    normal.filterMode = FilterMode.Bilinear;
                    material.SetTexture("_BumpMap", normal);
                }
                else if (normal == null)
                {
                    Debug.LogError("[HeatTilePainter] Missing normal map: " + definition.NormalPath);
                }
            }

            Color baseColor = definition.IsFlatColor ? tint : Color.white;
            baseColor.r = Mathf.Clamp01(baseColor.r);
            baseColor.g = Mathf.Clamp01(baseColor.g);
            baseColor.b = Mathf.Clamp01(baseColor.b);
            baseColor.a = deferredDecal ? 1f : opacity;
            if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", baseColor);
            material.color = baseColor;
            if (material.HasProperty("_DecalOpacity")) material.SetFloat("_DecalOpacity", opacity);
            if (material.HasProperty("_Opacity")) material.SetFloat("_Opacity", deferredDecal ? 1f : opacity);
            if (material.HasProperty("_Alpha")) material.SetFloat("_Alpha", opacity);
            if (material.HasProperty("_SpecColor"))
            {
                float s = Mathf.Clamp01(definition.Specularity);
                material.SetColor("_SpecColor", new Color(s, s, s, 1f));
            }
            if (material.HasProperty("_Shininess")) material.SetFloat("_Shininess", Mathf.Clamp01(definition.Shininess));
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", Mathf.Clamp01(definition.Shininess));
            if (deferredDecal)
            {
                material.EnableKeyword("DECAL_PREVIEW");
                if (hasNormal) material.EnableKeyword("DECAL_BUMPMAP");
                else material.DisableKeyword("DECAL_BUMPMAP");
                if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
                if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                material.renderQueue = 2100;
            }
            else
            {
                material.SetOverrideTag("RenderType", "Transparent");
                if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHATEST_ON");
                material.renderQueue = 2451;
            }
            material.SetShaderPassEnabled("SHADOWCASTER", false);

            string materialKind = definition.Id + (deferredDecal ? ":deferred-decal" : ":compatibility-overlay");
            if (LoggedMaterialKinds.Add(materialKind))
            {
                Debug.Log("[HeatTilePainter] Material " + materialKind + " uses shader " + shader.name
                    + ", requested opacity=" + opacity.ToString("F2", CultureInfo.InvariantCulture));
            }
            return material;
        }

        private static void ResolveDeferredDecalShader()
        {
            if (_decalShaderResolved) return;
            _decalShaderResolved = true;
            try { _decalShader = Shader.Find("ConformalDecals/Decal/Standard"); }
            catch (Exception) { _decalShader = null; }
            if (_decalShader == null)
            {
                try
                {
                    Type shabby = Type.GetType("Shabby.Shabby, Shabby", false);
                    MethodInfo findShader = shabby == null ? null : shabby.GetMethod("FindShader",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (findShader != null)
                        _decalShader = findShader.Invoke(null,
                            new object[] { "ConformalDecals/Decal/Standard" }) as Shader;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[HeatTilePainter] Could not query Shabby for the deferred decal shader: "
                        + exception.Message);
                }
            }
            Debug.Log(_decalShader != null
                ? "[HeatTilePainter] Rendering backend: Conformal Decals deferred shader (no camera follower)."
                : "[HeatTilePainter] Rendering backend: compatibility transparent overlay; install Conformal Decals + Shabby for the optimized deferred path.");
        }

        private static string ColorTextureProperty(Material material)
        {
            return material != null && material.HasProperty("_Decal") ? "_Decal" : "_MainTex";
        }

        private static void RestoreNormalizedUvs(Material material, string property,
            Vector2 range, Vector2 minimum)
        {
            if (material == null || !material.HasProperty(property)) return;
            Vector2 priorScale = material.GetTextureScale(property);
            Vector2 priorOffset = material.GetTextureOffset(property);
            material.SetTextureScale(property,
                new Vector2(priorScale.x * range.x, priorScale.y * range.y));
            material.SetTextureOffset(property,
                new Vector2(priorOffset.x + priorScale.x * minimum.x,
                    priorOffset.y + priorScale.y * minimum.y));
        }
#endif

        private static bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            return !string.IsNullOrEmpty(value) && bool.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string[] pieces = value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length < 3) return fallback;
            float red;
            float green;
            float blue;
            float alpha = 1f;
            if (!float.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out red)
                || !float.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out green)
                || !float.TryParse(pieces[2], NumberStyles.Float, CultureInfo.InvariantCulture, out blue)) return fallback;
            if (pieces.Length > 3) float.TryParse(pieces[3], NumberStyles.Float, CultureInfo.InvariantCulture, out alpha);
            return new Color(Mathf.Clamp01(red), Mathf.Clamp01(green), Mathf.Clamp01(blue), Mathf.Clamp01(alpha));
        }

        private static float ParseFloat(string value, float fallback)
        {
            float parsed;
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }
    }
}
