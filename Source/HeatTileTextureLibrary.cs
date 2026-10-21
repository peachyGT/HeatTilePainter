using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        public string ImportedFileName;
    }

    internal static class HeatTileTextureLibrary
    {
        private static readonly List<HeatTileTextureDefinition> DefinitionsInternal = new List<HeatTileTextureDefinition>();
        private static readonly List<HeatTileTextureDefinition> PaintDefinitionsInternal = new List<HeatTileTextureDefinition>();
        private static readonly Dictionary<string, Texture2D> ImportedTextures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DuplicateWarnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> MissingWarnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const string UserLibraryFolder = "HeatTileOverlayPrototype/PluginData/UserTextures";
        private const string UserManifestName = "UserTextures.cfg";
        private const long MaximumImportBytes = 32L * 1024L * 1024L;
        private const int MaximumImportDimension = 4096;
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

            ConfigNode[] nodes = GameDatabase.Instance != null
                ? GameDatabase.Instance.GetConfigNodes("HEAT_TILE_TEXTURE") : new ConfigNode[0];
            for (int i = 0; i < nodes.Length; i++)
            {
                HeatTileTextureDefinition definition = DefinitionFromNode(nodes[i], false);
                if (definition != null) AddOrReplaceDefinition(definition, true);
            }

            LoadImportedDefinitions();

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
            RebuildPaintDefinitions();
        }

        public static HeatTileTextureDefinition Find(string id)
        {
            HeatTileTextureDefinition definition;
            if (FindExact(id, out definition)) return definition;
            EnsureLoaded();
            return DefinitionsInternal[0];
        }

        public static bool FindExact(string id, out HeatTileTextureDefinition definition)
        {
            EnsureLoaded();
            for (int i = 0; i < DefinitionsInternal.Count; i++)
            {
                if (string.Equals(DefinitionsInternal[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    definition = DefinitionsInternal[i];
                    return true;
                }
            }
            definition = null;
            return false;
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

        public static string DisplayName(string id)
        {
            HeatTileTextureDefinition definition;
            return FindExact(id, out definition) ? definition.DisplayName : "Missing: " + id;
        }

        public static void WarnMissing(string id)
        {
            string safeId = string.IsNullOrEmpty(id) ? "(empty)" : id;
            if (MissingWarnings.Add(safeId))
                Debug.LogWarning("[HeatTilePainter] Paint definition '" + safeId
                    + "' is not installed; its saved layers were preserved but not rendered.");
        }

        public static bool TryResolveTexture(string id, bool normalMap, out Texture2D texture)
        {
            texture = null;
            HeatTileTextureDefinition definition;
            if (!FindExact(id, out definition) || definition.IsFlatColor) return false;
            if (!string.IsNullOrEmpty(definition.ImportedFileName))
            {
                if (normalMap) return false;
                return ImportedTextures.TryGetValue(definition.Id, out texture) && texture != null;
            }

            string path = normalMap ? definition.NormalPath : definition.TexturePath;
            if (string.IsNullOrEmpty(path) || GameDatabase.Instance == null) return false;
            texture = GameDatabase.Instance.GetTexture(path, normalMap);
            if (texture == null) return false;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            return true;
        }

        public static bool FindByTextureLeaf(string leaf, out HeatTileTextureDefinition definition)
        {
            EnsureLoaded();
            for (int i = 0; i < DefinitionsInternal.Count; i++)
            {
                string path = DefinitionsInternal[i].TexturePath;
                int slash = string.IsNullOrEmpty(path) ? -1 : path.LastIndexOf('/');
                string candidate = slash >= 0 ? path.Substring(slash + 1) : path;
                if (string.Equals(candidate, leaf, StringComparison.OrdinalIgnoreCase))
                {
                    definition = DefinitionsInternal[i];
                    return true;
                }
            }
            definition = null;
            return false;
        }

        public static bool TryImport(string sourcePath, out HeatTileTextureDefinition definition,
            out string error)
        {
            definition = null;
            error = "";
            Texture2D pendingTexture = null;
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    error = "Choose an image file that still exists.";
                    return false;
                }
                string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
                {
                    error = "Supported image types are PNG, JPG, and JPEG.";
                    return false;
                }

                if (new FileInfo(sourcePath).Length > MaximumImportBytes)
                {
                    error = "That image is larger than the 32 MB import limit.";
                    return false;
                }
                byte[] bytes = File.ReadAllBytes(sourcePath);
                if (!ImageImportPolicy.HasBoundedDimensions(bytes, MaximumImportDimension))
                {
                    error = "Choose a valid PNG or JPEG no larger than 4096 x 4096 pixels.";
                    return false;
                }
                Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                pendingTexture = decoded;
                if (!decoded.LoadImage(bytes, false))
                {
                    UnityEngine.Object.Destroy(decoded);
                    error = "KSP could not decode that image.";
                    return false;
                }
                if (decoded.width > MaximumImportDimension || decoded.height > MaximumImportDimension)
                {
                    UnityEngine.Object.Destroy(decoded);
                    error = "Custom paint images are limited to 4096 x 4096 pixels.";
                    return false;
                }
                decoded.wrapMode = TextureWrapMode.Repeat;
                decoded.filterMode = FilterMode.Bilinear;

                string hash = ContentHash(bytes);
                string stem = SafeId(Path.GetFileNameWithoutExtension(sourcePath));
                string id = "user_" + stem + "_" + hash.Substring(0, 8).ToLowerInvariant();
                HeatTileTextureDefinition collision;
                int collisionIndex = 0;
                string baseId = id;
                while (FindExact(id, out collision) && string.IsNullOrEmpty(collision.ImportedFileName))
                {
                    collisionIndex++;
                    id = baseId + "_" + collisionIndex.ToString(CultureInfo.InvariantCulture);
                }
                string storedName = id + extension;
                string destination = Path.Combine(UserLibraryPath, storedName);
                bool wroteDestination = false;
                try
                {
                    Directory.CreateDirectory(UserLibraryPath);
                    if (!File.Exists(destination))
                    {
                        File.WriteAllBytes(destination, bytes);
                        wroteDestination = true;
                    }
                }
                catch
                {
                    UnityEngine.Object.Destroy(decoded);
                    throw;
                }

                HeatTileTextureDefinition existing;
                if (FindExact(id, out existing))
                {
                    Texture2D prior;
                    ImportedTextures.TryGetValue(id, out prior);
                    string priorFileName = existing.ImportedFileName;
                    if (!string.Equals(priorFileName, storedName, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.ImportedFileName = storedName;
                        try { SaveImportedManifest(); }
                        catch
                        {
                            existing.ImportedFileName = priorFileName;
                            UnityEngine.Object.Destroy(decoded);
                            throw;
                        }
                    }
                    if (prior != null && prior != decoded) UnityEngine.Object.Destroy(prior);
                    ImportedTextures[id] = decoded;
                    pendingTexture = null;
                    ReforgedCore.WeatherCache.InvalidateOverlay(id);
                    definition = existing;
                    return true;
                }

                definition = new HeatTileTextureDefinition
                {
                    Id = id,
                    DisplayName = SafeDisplayName(Path.GetFileNameWithoutExtension(sourcePath)),
                    TexturePath = "",
                    NormalPath = "",
                    ImportedFileName = storedName,
                    IsFlatColor = false,
                    Hidden = false,
                    DefaultColor = Color.white,
                    PatternSpan = 1.28f,
                    Specularity = 0.04f,
                    Shininess = 0.06f
                };
                ImportedTextures[id] = decoded;
                AddOrReplaceDefinition(definition, false);
                RebuildPaintDefinitions();
                try { SaveImportedManifest(); }
                catch
                {
                    ImportedTextures.Remove(id);
                    RemoveDefinition(id);
                    UnityEngine.Object.Destroy(decoded);
                    if (wroteDestination)
                    {
                        try { File.Delete(destination); }
                        catch { }
                    }
                    throw;
                }
                pendingTexture = null;
                ReforgedCore.WeatherCache.InvalidateOverlay(id);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "KSP cannot write to the Heat Tile Painter user-texture folder.";
            }
            catch (IOException exception)
            {
                error = "The image could not be copied: " + exception.Message;
            }
            catch (Exception exception)
            {
                error = "The image could not be added: " + exception.Message;
            }
            finally { if (pendingTexture != null) UnityEngine.Object.Destroy(pendingTexture); }
            return false;
        }

        public static bool IsSupportedImagePath(string path)
        {
            string extension = string.IsNullOrEmpty(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg";
        }

        public static bool IsValidDefinitionId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 96) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.') continue;
                return false;
            }
            return true;
        }

        public static string UserLibraryPath
        {
            get
            {
                string root = KSPUtil.ApplicationRootPath;
                return Path.Combine(root, "GameData", UserLibraryFolder.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        private static string UserManifestPath
        {
            get { return Path.Combine(Path.GetDirectoryName(UserLibraryPath), UserManifestName); }
        }

        private static HeatTileTextureDefinition DefinitionFromNode(ConfigNode node, bool imported)
        {
            if (node == null) return null;
            string id = node.GetValue("id");
            string displayName = node.GetValue("displayName");
            string texture = node.GetValue("texture");
            string importedFile = imported ? node.GetValue("file") : "";
            bool isFlatColor = ParseBool(node.GetValue("flatColor"), false);
            if (!IsValidDefinitionId(id == null ? "" : id.Trim())
                || (!isFlatColor && string.IsNullOrEmpty(texture) && string.IsNullOrEmpty(importedFile))) return null;
            if (imported && Path.GetFileName(importedFile) != importedFile) return null;

            return new HeatTileTextureDefinition
            {
                Id = id.Trim(),
                DisplayName = string.IsNullOrEmpty(displayName) ? id.Trim() : displayName,
                TexturePath = texture,
                NormalPath = node.GetValue("normal"),
                ImportedFileName = importedFile,
                IsFlatColor = isFlatColor,
                Hidden = ParseBool(node.GetValue("hidden"), false),
                DefaultColor = ParseColor(node.GetValue("defaultColor"), new Color(0.165f, 0.165f, 0.165f, 1f)),
                PatternSpan = ParsePositiveFloat(node.GetValue("patternSpan"), 1.28f),
                Specularity = Mathf.Clamp01(ParseFloat(node.GetValue("specularity"), 0.06f)),
                Shininess = Mathf.Clamp01(ParseFloat(node.GetValue("shininess"), 0.08f))
            };
        }

        private static void AddOrReplaceDefinition(HeatTileTextureDefinition definition, bool warnDuplicate)
        {
            for (int i = 0; i < DefinitionsInternal.Count; i++)
            {
                if (!string.Equals(DefinitionsInternal[i].Id, definition.Id, StringComparison.OrdinalIgnoreCase)) continue;
                DefinitionsInternal[i] = definition;
                if (warnDuplicate && DuplicateWarnings.Add(definition.Id))
                    Debug.LogWarning("[HeatTilePainter] Duplicate HEAT_TILE_TEXTURE id '" + definition.Id
                        + "'; the last valid definition wins.");
                return;
            }
            DefinitionsInternal.Add(definition);
        }

        private static void RebuildPaintDefinitions()
        {
            PaintDefinitionsInternal.Clear();
            for (int i = 0; i < DefinitionsInternal.Count; i++)
                if (!DefinitionsInternal[i].Hidden) PaintDefinitionsInternal.Add(DefinitionsInternal[i]);
            if (PaintDefinitionsInternal.Count == 0 && DefinitionsInternal.Count > 0)
                PaintDefinitionsInternal.Add(DefinitionsInternal[0]);
        }

        private static void RemoveDefinition(string id)
        {
            for (int i = DefinitionsInternal.Count - 1; i >= 0; i--)
                if (string.Equals(DefinitionsInternal[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    DefinitionsInternal.RemoveAt(i);
            RebuildPaintDefinitions();
        }

        private static void LoadImportedDefinitions()
        {
            if (!File.Exists(UserManifestPath)) return;
            try
            {
                ConfigNode root = ConfigNode.Load(UserManifestPath);
                if (root == null) return;
                ConfigNode[] nodes = root.GetNodes("HEAT_TILE_TEXTURE");
                for (int i = 0; i < nodes.Length; i++)
                {
                    HeatTileTextureDefinition definition = DefinitionFromNode(nodes[i], true);
                    if (definition == null) continue;
                    AddOrReplaceDefinition(definition, false);
                    string filePath = Path.Combine(UserLibraryPath, definition.ImportedFileName);
                    Texture2D texture;
                    if (!TryDecodeFile(filePath, out texture)) continue;
                    ImportedTextures[definition.Id] = texture;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[HeatTilePainter] Could not load the custom texture library: "
                    + exception.GetType().Name + ".");
            }
        }

        private static void SaveImportedManifest()
        {
            ConfigNode root = new ConfigNode("HEAT_TILE_USER_LIBRARY");
            for (int i = 0; i < DefinitionsInternal.Count; i++)
            {
                HeatTileTextureDefinition definition = DefinitionsInternal[i];
                if (string.IsNullOrEmpty(definition.ImportedFileName)) continue;
                ConfigNode node = root.AddNode("HEAT_TILE_TEXTURE");
                node.AddValue("id", definition.Id);
                node.AddValue("displayName", definition.DisplayName);
                node.AddValue("file", definition.ImportedFileName);
                node.AddValue("patternSpan", definition.PatternSpan.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("specularity", definition.Specularity.ToString("R", CultureInfo.InvariantCulture));
                node.AddValue("shininess", definition.Shininess.ToString("R", CultureInfo.InvariantCulture));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(UserManifestPath));
            string temporary = UserManifestPath + ".tmp";
            root.Save(temporary);
            File.Copy(temporary, UserManifestPath, true);
            File.Delete(temporary);
        }

        private static bool TryDecodeFile(string path, out Texture2D texture)
        {
            texture = null;
            Texture2D pending = null;
            try
            {
                if (!File.Exists(path) || !IsSupportedImagePath(path)
                    || new FileInfo(path).Length > MaximumImportBytes) return false;
                byte[] bytes = File.ReadAllBytes(path);
                if (!ImageImportPolicy.HasBoundedDimensions(bytes, MaximumImportDimension)) return false;
                Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                pending = decoded;
                if (!decoded.LoadImage(bytes, false))
                {
                    UnityEngine.Object.Destroy(decoded);
                    return false;
                }
                if (decoded.width > MaximumImportDimension || decoded.height > MaximumImportDimension)
                {
                    UnityEngine.Object.Destroy(decoded);
                    return false;
                }
                decoded.wrapMode = TextureWrapMode.Repeat;
                decoded.filterMode = FilterMode.Bilinear;
                texture = decoded;
                pending = null;
                return true;
            }
            catch { return false; }
            finally { if (pending != null) UnityEngine.Object.Destroy(pending); }
        }

        private static string ContentHash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static string SafeId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "texture";
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < value.Length && result.Length < 40; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) result.Append(c);
                else if (result.Length > 0 && result[result.Length - 1] != '_') result.Append('_');
            }
            string id = result.ToString().Trim('_');
            return string.IsNullOrEmpty(id) ? "texture" : id;
        }

        private static string SafeDisplayName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Custom texture";
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < value.Length && result.Length < 80; i++)
            {
                char c = value[i];
                if (char.IsControl(c) || c == '{' || c == '}') continue;
                result.Append(c);
            }
            string display = result.ToString().Trim();
            return string.IsNullOrEmpty(display) ? "Custom texture" : display;
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
            if (float.IsNaN(red) || float.IsInfinity(red) || float.IsNaN(green) || float.IsInfinity(green)
                || float.IsNaN(blue) || float.IsInfinity(blue) || float.IsNaN(alpha) || float.IsInfinity(alpha)) return fallback;
            return new Color(Mathf.Clamp01(red), Mathf.Clamp01(green), Mathf.Clamp01(blue), Mathf.Clamp01(alpha));
        }

        private static float ParseFloat(string value, float fallback)
        {
            float parsed;
            if (!string.IsNullOrEmpty(value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                && !float.IsNaN(parsed) && !float.IsInfinity(parsed)) return parsed;
            return fallback;
        }

        private static float ParsePositiveFloat(string value, float fallback)
        {
            float parsed = ParseFloat(value, fallback);
            return float.IsNaN(parsed) || float.IsInfinity(parsed) || parsed <= 0f
                ? fallback : Mathf.Max(0.05f, parsed);
        }
    }
}
