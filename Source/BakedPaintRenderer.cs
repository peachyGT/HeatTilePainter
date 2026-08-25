using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace HeatTileOverlayPrototype
{
    // Zero-extra-pass material compiler. The cache and bounded load strategy are adapted from
    // Reforged Materials Redux by J4COB (MIT); our spatial masks and ordered layer schema remain
    // Heat Tile Painter-specific.
    public sealed partial class ModuleHeatTileOverlay
    {
        private const int MaximumBakeDimension = 1024;
        private const int UntexturedBakeDimension = 512;
        private const float RasterEpsilon = -0.0001f;

        private sealed class BakedMaterialBinding
        {
            public MeshRenderer Renderer;
            public Material[] Original;
            public Material[] Applied;
            public readonly List<BakedCacheEntry> CacheEntries = new List<BakedCacheEntry>();
        }

        private sealed class BakedCacheEntry
        {
            public string Key;
            public Material Material;
            public Texture2D Texture;
            public Texture2D NormalTexture;
            public int ChangedTexels;
            public int RasterizedTriangles;
            public int RefCount;
            public int LastUsedFrame;
            public long EstimatedBytes;
            public bool MaterialOnly;
        }

        private sealed class TexturePixels
        {
            public Color32[] Pixels;
            public int Width;
            public int Height;
        }

        private sealed class LayerRaster
        {
            public float[] Alpha;
            public Color32[] Color;
        }

        private readonly List<BakedMaterialBinding> _bakedBindings =
            new List<BakedMaterialBinding>();
        private readonly List<Material> _bakedPaintMaterials = new List<Material>();
        private readonly List<Texture2D> _bakedPaintTextures = new List<Texture2D>();
        private static readonly Dictionary<string, TexturePixels> PatternPixelCache =
            new Dictionary<string, TexturePixels>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TexturePixels> SourcePixelCache =
            new Dictionary<string, TexturePixels>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BakedCacheEntry> BakedMaterialCache =
            new Dictionary<string, BakedCacheEntry>(StringComparer.Ordinal);
        private const int MaximumCachedBakes = 96;
        private const long MaximumBakedCacheBytes = 256L * 1024L * 1024L;
        private static long _bakedCacheBytes;
        private ulong _bakedSourceState;
        private bool _bakedSourceStateRecorded;
        private int _nextSourceStateCheck;

        private void RecordBakedSourceState()
        {
            _bakedSourceState = ComputeBakedSourceState();
            _bakedSourceStateRecorded = true;
            _nextSourceStateCheck = Time.frameCount + 15;
        }

        private bool HasBakedSourceStateChanged()
        {
            return !_bakedSourceStateRecorded || ComputeBakedSourceState() != _bakedSourceState;
        }

        private void MonitorBakedSourceState()
        {
            if (!_requiresSourceMonitoring || !HighLogic.LoadedSceneIsEditor || !_bakedSourceStateRecorded
                || _layers.Count == 0 || Time.frameCount < _nextSourceStateCheck) return;
            _nextSourceStateCheck = Time.frameCount + 15;
            if (ComputeBakedSourceState() != _bakedSourceState) RequestRebuild(3);
        }

        private ulong ComputeBakedSourceState()
        {
            ulong hash = 1469598103934665603UL;
            if (part == null) return hash;
            MeshFilter[] filters = part.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter == null || filter.sharedMesh == null || IsGeneratedObject(filter.transform)) continue;
                if (filter.GetComponentInParent<Part>() != part) continue;
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null) continue;
                Mesh mesh = filter.sharedMesh;
                HashValue(ref hash, mesh.GetInstanceID());
                HashValue(ref hash, mesh.vertexCount);
                HashValue(ref hash, mesh.subMeshCount);
                Bounds bounds = mesh.bounds;
                HashVector(ref hash, bounds.center);
                HashVector(ref hash, bounds.extents);
                HashVector(ref hash, filter.transform.localPosition);
                HashVector(ref hash, filter.transform.localScale);
                Quaternion rotation = filter.transform.localRotation;
                HashFloat(ref hash, rotation.x); HashFloat(ref hash, rotation.y);
                HashFloat(ref hash, rotation.z); HashFloat(ref hash, rotation.w);
                Material[] materials = renderer.sharedMaterials;
                HashValue(ref hash, materials == null ? 0 : materials.Length);
                if (materials != null)
                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                        HashValue(ref hash, materials[materialIndex] == null ? 0 : materials[materialIndex].GetInstanceID());
            }
            return hash;
        }

        private static void HashVector(ref ulong hash, Vector3 value)
        {
            HashFloat(ref hash, value.x); HashFloat(ref hash, value.y); HashFloat(ref hash, value.z);
        }

        private static void HashFloat(ref ulong hash, float value)
        {
            HashValue(ref hash, Mathf.RoundToInt(value * 100000f));
        }

        private static void HashValue(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
            }
        }

        private bool TryRebuildBakedPaint()
        {
            DateTime started = DateTime.UtcNow;
            List<MeshSource> sources = FindMeshSources();
            if (sources.Count == 0) return false;

            List<HeatTileLayerState> visibleLayers = new List<HeatTileLayerState>();
            List<CoverageContext> contexts = new List<CoverageContext>();
            for (int i = 0; i < _layers.Count; i++)
            {
                HeatTileLayerState layer = _layers[i];
                if (!layer.Visible || layer.Coverage <= 0.001f || layer.Opacity <= 0.001f) continue;
                CoverageContext context = CreateCoverageContext(sources, layer);
                if (context == null) continue;
                visibleLayers.Add(layer);
                contexts.Add(context);
            }
            if (visibleLayers.Count == 0) return true;

            // Separate top/bottom/edge material groups are common on B9 procedural wings and some
            // mod cockpits. When every group is wholly inside or outside every layer, a material
            // swap is exact and the generator keeps ownership of its mesh.
            if (TryRebuildUniformSurfacePaint(sources, visibleLayers, contexts)) return true;

            // Region materials keep pattern normals and matte/gloss properties local to the
            // painted geometry instead of changing the entire source material. Use that path for
            // every bounded stack when topology ownership is safe. B9 Procedural Wings retain
            // their generator-owned material/UV adapter; other procedural parts use a monitored
            // generated-mesh snapshot.
            if (ShouldUseFlatRegionPaint(visibleLayers)
                && TryRebuildFlatRegionPaint(sources, visibleLayers, contexts)) return true;

            // A source-albedo bake cannot distinguish geometric surfaces that reuse the same UV
            // triangle. Flat hard coats can resolve that case by splitting the source mesh into
            // cached material regions. Patterned, feathered, and larger stacks use the exact
            // clipped-geometry fallback only when such a conflict is actually present; ordinary
            // parts retain the zero-extra-pass texture bake.
            bool sharedUvConflict = NeedsSharedUvPaintIsolation(sources, contexts, visibleLayers);
            if (sharedUvConflict)
            {
                if (ShouldUseFlatRegionPaint(visibleLayers)
                    && TryRebuildFlatRegionPaint(sources, visibleLayers, contexts)) return true;
                Debug.LogWarning("[HeatTilePainter] Shared UVs on procedural/mutable part "
                    + part.partInfo.name + " cannot be isolated without replacing generator-owned topology; "
                    + "paint was safely declined.");
                return false;
            }

            int bakedMaterialCount = 0;
            int cacheHits = 0;
            int changedTexels = 0;
            int rasterizedTriangles = 0;
            bool hardFlatLayers = AreHardFlatLayers(visibleLayers);
            try
            {
                for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                {
                    MeshSource source = sources[sourceIndex];
                    if (source == null || source.Mesh == null || source.Renderer == null) continue;
                    Vector2[] uvs;
                    Vector3[] sourceVertices;
                    Vector3[] sourceNormals;
                    try
                    {
                        uvs = source.Mesh.uv;
                        sourceVertices = source.Mesh.vertices;
                        sourceNormals = source.Mesh.normals;
                    }
                    catch (Exception)
                    {
                        RestoreBakedPaint();
                        return false;
                    }
                    if (uvs == null || uvs.Length != sourceVertices.Length || uvs.Length == 0)
                    {
                        Debug.LogWarning("[HeatTilePainter] Texture bake cannot use " + source.Key
                            + " because its mesh has no complete UV0 channel; paint was safely declined.");
                        RestoreBakedPaint();
                        return false;
                    }

                    Vector3[] partVertices = new Vector3[sourceVertices.Length];
                    Vector3[] partNormals = new Vector3[sourceVertices.Length];
                    bool hasNormals = sourceNormals != null && sourceNormals.Length == sourceVertices.Length;
                    Matrix4x4 normalToPart = source.SourceToPart.inverse.transpose;
                    for (int i = 0; i < sourceVertices.Length; i++)
                    {
                        partVertices[i] = source.SourceToPart.MultiplyPoint3x4(sourceVertices[i]);
                        partNormals[i] = hasNormals
                            ? normalToPart.MultiplyVector(sourceNormals[i]).normalized : Vector3.zero;
                    }

                    Material[] original = source.Renderer.sharedMaterials;
                    if (original == null || original.Length == 0) continue;
                    Material[] applied = new Material[original.Length];
                    Array.Copy(original, applied, original.Length);
                    BakedMaterialBinding binding = new BakedMaterialBinding
                    {
                        Renderer = source.Renderer,
                        Original = original,
                        Applied = applied
                    };
                    _bakedBindings.Add(binding);

                    Dictionary<int, List<int>> submeshesByMaterial = new Dictionary<int, List<int>>();
                    for (int subMesh = 0; subMesh < source.Mesh.subMeshCount; subMesh++)
                    {
                        if (!IsEligibleSubmesh(source, subMesh)) continue;
                        int materialIndex = Mathf.Min(subMesh, original.Length - 1);
                        List<int> group;
                        if (!submeshesByMaterial.TryGetValue(materialIndex, out group))
                        {
                            group = new List<int>();
                            submeshesByMaterial.Add(materialIndex, group);
                        }
                        group.Add(subMesh);
                    }

                    foreach (KeyValuePair<int, List<int>> pair in submeshesByMaterial)
                    {
                        int uniformMask;
                        if (hardFlatLayers && TryGetUniformFlatMask(source, pair.Value,
                            partVertices, partNormals, hasNormals, contexts, out uniformMask))
                        {
                            if (uniformMask == 0) continue;
                            Material uniformMaterial;
                            BakedCacheEntry uniformEntry;
                            bool uniformCacheHit;
                            if (!TryGetFlatRegionMaterial(source, original[pair.Key], visibleLayers,
                                uniformMask, out uniformMaterial, out uniformEntry,
                                out uniformCacheHit))
                            {
                                RestoreBakedPaint();
                                return false;
                            }
                            applied[pair.Key] = uniformMaterial;
                            uniformEntry.RefCount++;
                            uniformEntry.LastUsedFrame = Time.frameCount;
                            binding.CacheEntries.Add(uniformEntry);
                            bakedMaterialCount++;
                            if (uniformCacheHit) cacheHits++;
                            continue;
                        }
                        Material bakedMaterial;
                        Texture2D bakedTexture;
                        BakedCacheEntry cacheEntry;
                        int localChanged;
                        int localTriangles;
                        if (!TryBakeMaterial(source, original[pair.Key], pair.Value, uvs,
                            partVertices, partNormals, visibleLayers, contexts,
                            out bakedMaterial, out bakedTexture, out cacheEntry,
                            out localChanged, out localTriangles))
                        {
                            Debug.LogWarning("[HeatTilePainter] Source material " + pair.Key + " on "
                                + source.Key + " is not material-bake compatible; paint was safely declined.");
                            RestoreBakedPaint();
                            return false;
                        }
                        applied[pair.Key] = bakedMaterial;
                        cacheEntry.RefCount++;
                        cacheEntry.LastUsedFrame = Time.frameCount;
                        binding.CacheEntries.Add(cacheEntry);
                        bakedMaterialCount++;
                        if (localTriangles == 0) cacheHits++;
                        changedTexels += localChanged;
                        rasterizedTriangles += localTriangles;
                    }
                    source.Renderer.sharedMaterials = applied;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[HeatTilePainter] Texture bake failed on " + part.partInfo.name
                    + ": " + exception.Message + "; paint was safely declined.");
                RestoreBakedPaint();
                return false;
            }

            Debug.Log("[HeatTilePainter] Baked " + visibleLayers.Count + " layer(s) into "
                + bakedMaterialCount + " source material(s) on " + part.partInfo.name
                + ": changedTexels=" + changedTexels.ToString(CultureInfo.InvariantCulture)
                + ", rasterizedTriangles=" + rasterizedTriangles.ToString(CultureInfo.InvariantCulture)
                + ", cacheHits=" + cacheHits.ToString(CultureInfo.InvariantCulture)
                + ", milliseconds=" + (DateTime.UtcNow - started).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)
                + ", persistentOverlayRenderers=0, additionalDrawPasses=0.");
            return true;
        }

        private bool TryBakeMaterial(MeshSource source, Material original, IList<int> submeshes,
            Vector2[] uvs, Vector3[] partVertices, Vector3[] partNormals,
            IList<HeatTileLayerState> layers, IList<CoverageContext> contexts,
            out Material bakedMaterial, out Texture2D bakedTexture, out BakedCacheEntry cacheEntry,
            out int changedTexels, out int rasterizedTriangles)
        {
            bakedMaterial = null;
            bakedTexture = null;
            cacheEntry = null;
            changedTexels = 0;
            rasterizedTriangles = 0;
            if (original == null || !original.HasProperty("_MainTex")) return false;

            Texture sourceTexture = original.GetTexture("_MainTex");
            int width = sourceTexture == null ? UntexturedBakeDimension
                : Mathf.Clamp(sourceTexture.width, 16, MaximumBakeDimension);
            int height = sourceTexture == null ? UntexturedBakeDimension
                : Mathf.Clamp(sourceTexture.height, 16, MaximumBakeDimension);
            Color materialTint = original.HasProperty("_Color")
                ? original.GetColor("_Color") : Color.white;
            Vector2 textureScale = original.GetTextureScale("_MainTex");
            Vector2 textureOffset = original.GetTextureOffset("_MainTex");
            bool repeat = sourceTexture == null || sourceTexture.wrapMode == TextureWrapMode.Repeat;

            string bakeKey = BuildBakeCacheKey(source, original, sourceTexture, submeshes,
                uvs, partVertices, layers, contexts, textureScale, textureOffset);
            if (BakedMaterialCache.TryGetValue(bakeKey, out cacheEntry)
                && cacheEntry != null && cacheEntry.Material != null && cacheEntry.Texture != null)
            {
                bakedMaterial = cacheEntry.Material;
                bakedTexture = cacheEntry.Texture;
                changedTexels = cacheEntry.ChangedTexels;
                rasterizedTriangles = 0;
                return true;
            }
            if (cacheEntry != null)
            {
                BakedMaterialCache.Remove(bakeKey);
                _bakedCacheBytes = Math.Max(0L, _bakedCacheBytes - cacheEntry.EstimatedBytes);
                if (cacheEntry.Material != null) Destroy(cacheEntry.Material);
                if (cacheEntry.Texture != null) Destroy(cacheEntry.Texture);
                if (cacheEntry.NormalTexture != null) Destroy(cacheEntry.NormalTexture);
                cacheEntry = null;
            }

            TexturePixels basePixels = SourcePixelsFor(sourceTexture, width, height);
            if (basePixels == null || basePixels.Pixels == null) return false;
            Color32[] originalPixels = basePixels.Pixels;
            Color32[] composite = new Color32[originalPixels.Length];
            Array.Copy(originalPixels, composite, originalPixels.Length);
            bool usesIsolation = false;
            if (layers.Count > 1)
                for (int i = 0; i < layers.Count; i++)
                    if (layers[i].IsolateOpacity) { usesIsolation = true; break; }
            bool[] isolatedFootprint = usesIsolation ? new bool[originalPixels.Length] : null;
            int[] changedByRow = new int[height];

            for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                HeatTileLayerState layer = layers[layerIndex];
                CoverageContext context = contexts[layerIndex];
                HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
                // Flat coats need a geometric mask, not a million independently rasterized texels.
                // A 512px mask is expanded over the untouched full-resolution source albedo;
                // patterned paint retains the full bake resolution.
                int rasterWidth = definition.IsFlatColor ? Mathf.Min(width, 512) : width;
                int rasterHeight = definition.IsFlatColor ? Mathf.Min(height, 512) : height;
                LayerRaster raster = new LayerRaster
                {
                    Alpha = new float[rasterWidth * rasterHeight],
                    Color = new Color32[rasterWidth * rasterHeight]
                };
                TexturePixels pattern = PatternPixelsFor(layer.TextureId);
                for (int groupIndex = 0; groupIndex < submeshes.Count; groupIndex++)
                {
                    int[] triangles;
                    try { triangles = source.Mesh.GetTriangles(submeshes[groupIndex]); }
                    catch (Exception) { continue; }
                    for (int tri = 0; tri + 2 < triangles.Length; tri += 3)
                    {
                        int ia = triangles[tri];
                        int ib = triangles[tri + 1];
                        int ic = triangles[tri + 2];
                        if (ia < 0 || ib < 0 || ic < 0 || ia >= uvs.Length || ib >= uvs.Length || ic >= uvs.Length)
                            continue;
                        RasterizeTriangle(layer, context, pattern, raster, rasterWidth, rasterHeight,
                            TransformUv(uvs[ia], textureScale, textureOffset),
                            TransformUv(uvs[ib], textureScale, textureOffset),
                            TransformUv(uvs[ic], textureScale, textureOffset),
                            partVertices[ia], partVertices[ib], partVertices[ic],
                            partNormals[ia], partNormals[ib], partNormals[ic], repeat);
                        rasterizedTriangles++;
                    }
                }

                bool countFinalChanges = layerIndex == layers.Count - 1;
                ForRows(height, delegate(int outputY)
                {
                    int rowChanges = 0;
                    int rasterY = Mathf.Min(rasterHeight - 1,
                        Mathf.FloorToInt((outputY + 0.5f) * rasterHeight / height));
                    for (int outputX = 0; outputX < width; outputX++)
                    {
                        int rasterX = Mathf.Min(rasterWidth - 1,
                            Mathf.FloorToInt((outputX + 0.5f) * rasterWidth / width));
                        int rasterPixel = rasterY * rasterWidth + rasterX;
                        float alpha = raster.Alpha[rasterPixel];
                        int pixel = outputY * width + outputX;
                        if (alpha > 0.0001f)
                        {
                            // Isolation is symmetric at an overlap. Toggling either participant
                            // prevents two translucent coats from multiplying: the visually upper
                            // coat is blended once against the untouched source texel.
                            if (usesIsolation && (layer.IsolateOpacity || isolatedFootprint[pixel]))
                                composite[pixel] = originalPixels[pixel];
                            Color sourceColor = composite[pixel];
                            Color targetColor = CorrectForMaterialTint(raster.Color[rasterPixel], materialTint);
                            Color result = Color.Lerp(sourceColor, targetColor, Mathf.Clamp01(alpha));
                            Color32 encoded = result;
                            encoded.a = originalPixels[pixel].a;
                            composite[pixel] = encoded;
                            if (usesIsolation) isolatedFootprint[pixel] = layer.IsolateOpacity;
                        }
                        if (countFinalChanges
                            && (composite[pixel].r != originalPixels[pixel].r
                                || composite[pixel].g != originalPixels[pixel].g
                                || composite[pixel].b != originalPixels[pixel].b)) rowChanges++;
                    }
                    if (countFinalChanges) changedByRow[outputY] = rowChanges;
                });
            }

            for (int row = 0; row < changedByRow.Length; row++) changedTexels += changedByRow[row];

            bakedTexture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            bakedTexture.name = original.name + "-HeatTilePainter-Baked";
            bakedTexture.wrapMode = sourceTexture == null ? TextureWrapMode.Repeat : sourceTexture.wrapMode;
            bakedTexture.filterMode = sourceTexture == null ? FilterMode.Bilinear : sourceTexture.filterMode;
            bakedTexture.anisoLevel = sourceTexture == null ? 1 : sourceTexture.anisoLevel;
            bakedTexture.SetPixels32(composite);
            // The generated albedo is never sampled by CPU code again. Discarding the upload copy
            // prevents every cached 1024px bake from occupying both managed/native CPU and GPU memory.
            bakedTexture.Apply(true, true);

            bakedMaterial = new Material(original);
            bakedMaterial.name = original.name + "-HeatTilePainter-Baked";
            bakedMaterial.SetTexture("_MainTex", bakedTexture);
            cacheEntry = new BakedCacheEntry
            {
                Key = bakeKey,
                Material = bakedMaterial,
                Texture = bakedTexture,
                ChangedTexels = changedTexels,
                RasterizedTriangles = rasterizedTriangles,
                LastUsedFrame = Time.frameCount,
                EstimatedBytes = EstimateTextureBytes(width, height)
            };
            EvictUnusedBakeIfNeeded(cacheEntry.EstimatedBytes);
            BakedMaterialCache[bakeKey] = cacheEntry;
            _bakedCacheBytes += cacheEntry.EstimatedBytes;
            return true;
        }

        private static void ForRows(int rowCount, Action<int> action)
        {
            if (rowCount >= 128 && Environment.ProcessorCount > 1)
                Parallel.For(0, rowCount, action);
            else
                for (int row = 0; row < rowCount; row++) action(row);
        }

        private static bool HasPatternedPaint(IList<HeatTileLayerState> layers)
        {
            if (layers == null) return false;
            for (int i = 0; i < layers.Count; i++)
            {
                HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(
                    layers[i].TextureId);
                if (definition != null && !definition.IsFlatColor) return true;
            }
            return false;
        }

        private static long EstimateTextureBytes(int width, int height)
        {
            // RGBA32 plus the complete mip chain (approximately 4/3 of the base level).
            return (long)width * height * 16L / 3L;
        }

        private void RasterizeTriangle(HeatTileLayerState layer, CoverageContext context,
            TexturePixels pattern, LayerRaster raster, int width, int height,
            Vector2 uvA, Vector2 uvB, Vector2 uvC,
            Vector3 positionA, Vector3 positionB, Vector3 positionC,
            Vector3 normalA, Vector3 normalB, Vector3 normalC, bool repeat)
        {
            if (repeat)
            {
                uvB.x = uvA.x + RepeatDelta(uvB.x - uvA.x);
                uvB.y = uvA.y + RepeatDelta(uvB.y - uvA.y);
                uvC.x = uvA.x + RepeatDelta(uvC.x - uvA.x);
                uvC.y = uvA.y + RepeatDelta(uvC.y - uvA.y);
            }
            int shiftXMin = repeat ? Mathf.CeilToInt(-Mathf.Max(uvA.x, Mathf.Max(uvB.x, uvC.x))) : 0;
            int shiftXMax = repeat ? Mathf.FloorToInt(1f - Mathf.Min(uvA.x, Mathf.Min(uvB.x, uvC.x))) : 0;
            int shiftYMin = repeat ? Mathf.CeilToInt(-Mathf.Max(uvA.y, Mathf.Max(uvB.y, uvC.y))) : 0;
            int shiftYMax = repeat ? Mathf.FloorToInt(1f - Mathf.Min(uvA.y, Mathf.Min(uvB.y, uvC.y))) : 0;
            shiftXMin = Mathf.Max(shiftXMin, -2); shiftXMax = Mathf.Min(shiftXMax, 2);
            shiftYMin = Mathf.Max(shiftYMin, -2); shiftYMax = Mathf.Min(shiftYMax, 2);

            for (int shiftY = shiftYMin; shiftY <= shiftYMax; shiftY++)
                for (int shiftX = shiftXMin; shiftX <= shiftXMax; shiftX++)
                {
                    Vector2 a = uvA + new Vector2(shiftX, shiftY);
                    Vector2 b = uvB + new Vector2(shiftX, shiftY);
                    Vector2 c = uvC + new Vector2(shiftX, shiftY);
                    float denominator = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                    if (Math.Abs(denominator) < 0.00000001f) continue;
                    int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) * width), 0, width - 1);
                    int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) * width), 0, width - 1);
                    int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) * height), 0, height - 1);
                    int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) * height), 0, height - 1);
                    for (int y = minY; y <= maxY; y++)
                    {
                        float v = (y + 0.5f) / height;
                        for (int x = minX; x <= maxX; x++)
                        {
                            float u = (x + 0.5f) / width;
                            float wa = ((b.y - c.y) * (u - c.x) + (c.x - b.x) * (v - c.y)) / denominator;
                            float wb = ((c.y - a.y) * (u - c.x) + (a.x - c.x) * (v - c.y)) / denominator;
                            float wc = 1f - wa - wb;
                            if (wa < RasterEpsilon || wb < RasterEpsilon || wc < RasterEpsilon) continue;
                            Vector3 position = positionA * wa + positionB * wb + positionC * wc;
                            Vector3 normal = (normalA * wa + normalB * wb + normalC * wc).normalized;
                            if (normal.sqrMagnitude < 0.5f)
                                normal = Vector3.Cross(positionB - positionA, positionC - positionA).normalized;
                            float mask = CoverageWeight(position, normal, context);
                            float alpha = mask * Mathf.Clamp01(layer.Opacity / 100f);
                            int pixel = y * width + x;
                            if (alpha <= raster.Alpha[pixel]) continue;
                            raster.Alpha[pixel] = alpha;
                            raster.Color[pixel] = PaintColorAt(layer, pattern, position, normal);
                        }
                    }
                }
        }

        private float CoverageWeight(Vector3 position, Vector3 normal, CoverageContext context)
        {
            float weightA = DirectionWeight(position, context.DirectionA, context.ClipA,
                context.BlendA, context.State.EdgeStyle);
            if (context.State.DirectionFacingOnly)
                weightA *= FacingWeight(normal, context.DirectionA, context.State.SurfaceWrap);
            if (!context.Mirrored) return weightA;
            float weightB = DirectionWeight(position, context.DirectionB, context.ClipB,
                context.BlendB, context.State.EdgeStyle);
            if (context.State.DirectionFacingOnly)
                weightB *= FacingWeight(normal, context.DirectionB, context.State.SurfaceWrap);
            return context.State.MirrorMode == 1 ? Mathf.Max(weightA, weightB) : Mathf.Min(weightA, weightB);
        }

        private static float DirectionWeight(Vector3 position, Vector3 direction,
            float clip, float blend, int edgeStyle)
        {
            float distance = Vector3.Dot(position, direction) - clip;
            if (distance < 0f) return 0f;
            if (edgeStyle == 1 && blend > 0.00001f)
            {
                float t = Mathf.Clamp01(distance / blend);
                return t * t * (3f - 2f * t);
            }
            return 1f;
        }

        private static float FacingWeight(Vector3 normal, Vector3 direction, float wrapDegrees)
        {
            float threshold = Mathf.Cos(wrapDegrees * Mathf.Deg2Rad);
            float value = Vector3.Dot(normal, direction);
            // A tiny angular transition is enough to suppress UV/triangle stair-stepping without
            // visibly broadening the user's chosen surface-wrap boundary.
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(threshold - 0.015f,
                threshold + 0.015f, value));
        }

        private Color32 PaintColorAt(HeatTileLayerState layer, TexturePixels pattern,
            Vector3 position, Vector3 normal)
        {
            HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
            if (definition.IsFlatColor || pattern == null || pattern.Pixels == null)
                return (Color32)layer.Tint;
            Vector2 uv = CalculateUv(position, normal, Mathf.Max(0.1f, definition.PatternSpan));
            return SampleBilinear(pattern, uv.x, uv.y);
        }

        private static Color32 SampleBilinear(TexturePixels texture, float u, float v)
        {
            u = Mathf.Repeat(u, 1f);
            v = Mathf.Repeat(v, 1f);
            float px = u * texture.Width - 0.5f;
            float py = v * texture.Height - 0.5f;
            int x0 = Mathf.FloorToInt(px);
            int y0 = Mathf.FloorToInt(py);
            float tx = px - x0;
            float ty = py - y0;
            int x1 = Mod(x0 + 1, texture.Width);
            int y1 = Mod(y0 + 1, texture.Height);
            x0 = Mod(x0, texture.Width);
            y0 = Mod(y0, texture.Height);
            Color a = Color.Lerp(texture.Pixels[y0 * texture.Width + x0],
                texture.Pixels[y0 * texture.Width + x1], tx);
            Color b = Color.Lerp(texture.Pixels[y1 * texture.Width + x0],
                texture.Pixels[y1 * texture.Width + x1], tx);
            return (Color32)Color.Lerp(a, b, ty);
        }

        private static int Mod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static Vector2 TransformUv(Vector2 uv, Vector2 scale, Vector2 offset)
        {
            return new Vector2(uv.x * scale.x + offset.x, uv.y * scale.y + offset.y);
        }

        private static float RepeatDelta(float value)
        {
            return value - Mathf.Floor(value + 0.5f);
        }

        private static Color CorrectForMaterialTint(Color32 requested, Color materialTint)
        {
            Color value = requested;
            value.r = materialTint.r > 0.001f ? Mathf.Clamp01(value.r / materialTint.r) : value.r;
            value.g = materialTint.g > 0.001f ? Mathf.Clamp01(value.g / materialTint.g) : value.g;
            value.b = materialTint.b > 0.001f ? Mathf.Clamp01(value.b / materialTint.b) : value.b;
            value.a = 1f;
            return value;
        }

        private static TexturePixels PatternPixelsFor(string textureId)
        {
            HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(textureId);
            if (definition == null || definition.IsFlatColor || string.IsNullOrEmpty(definition.TexturePath)) return null;
            TexturePixels cached;
            if (PatternPixelCache.TryGetValue(definition.TexturePath, out cached)) return cached;
            Texture2D texture = GameDatabase.Instance.GetTexture(definition.TexturePath, false);
            if (texture == null) return null;
            cached = ReadTexturePixels(texture,
                Mathf.Clamp(texture.width, 16, MaximumBakeDimension),
                Mathf.Clamp(texture.height, 16, MaximumBakeDimension));
            if (cached != null) PatternPixelCache[definition.TexturePath] = cached;
            return cached;
        }

        private static TexturePixels NormalPixelsFor(string textureId)
        {
            HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(textureId);
            if (definition == null || definition.IsFlatColor || string.IsNullOrEmpty(definition.NormalPath))
                return null;
            string key = "normal:" + definition.NormalPath;
            TexturePixels cached;
            if (PatternPixelCache.TryGetValue(key, out cached)) return cached;
            // Read the authored pixels as colour data, matching Reforged's runtime normal
            // compositing path. The cloned source material continues to interpret the result using
            // its original bump-map shader convention.
            Texture2D texture = GameDatabase.Instance.GetTexture(definition.NormalPath, false);
            if (texture == null) return null;
            cached = ReadTexturePixels(texture,
                Mathf.Clamp(texture.width, 16, MaximumBakeDimension),
                Mathf.Clamp(texture.height, 16, MaximumBakeDimension));
            if (cached != null) PatternPixelCache[key] = cached;
            return cached;
        }

        private static TexturePixels SourcePixelsFor(Texture texture, int width, int height)
        {
            string key = (texture == null ? 0 : texture.GetInstanceID()).ToString(CultureInfo.InvariantCulture)
                + ":" + width.ToString(CultureInfo.InvariantCulture)
                + "x" + height.ToString(CultureInfo.InvariantCulture);
            TexturePixels cached;
            if (SourcePixelCache.TryGetValue(key, out cached)) return cached;
            cached = ReadTexturePixels(texture, width, height);
            // Avoid an unbounded second copy of all GameData textures. The most common shared
            // albedos are encountered first and provide the useful load-time wins.
            if (cached != null && SourcePixelCache.Count < 24) SourcePixelCache[key] = cached;
            return cached;
        }

        private static TexturePixels ReadTexturePixels(Texture source, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            if (source == null)
            {
                Color32[] white = new Color32[width * height];
                Color32 value = new Color32(255, 255, 255, 255);
                for (int i = 0; i < white.Length; i++) white[i] = value;
                return new TexturePixels { Pixels = white, Width = width, Height = height };
            }
            Texture2D readable = source as Texture2D;
            if (readable != null && readable.isReadable && readable.width == width && readable.height == height)
            {
                try
                {
                    return new TexturePixels { Pixels = readable.GetPixels32(), Width = width, Height = height };
                }
                catch (Exception) { }
            }

            RenderTexture prior = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D copy = null;
            try
            {
                temporary = RenderTexture.GetTemporary(width, height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                copy = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                copy.Apply(false, false);
                return new TexturePixels { Pixels = copy.GetPixels32(), Width = width, Height = height };
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                RenderTexture.active = prior;
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }

        private void RestoreBakedPaint()
        {
            RestoreFlatRegionPaint();
            for (int i = 0; i < _bakedBindings.Count; i++)
            {
                BakedMaterialBinding binding = _bakedBindings[i];
                if (binding == null) continue;
                if (binding.Renderer != null)
                {
                    Material[] current = binding.Renderer.sharedMaterials;
                    if (SameMaterialArray(current, binding.Applied))
                        binding.Renderer.sharedMaterials = binding.Original;
                }
                for (int cacheIndex = 0; cacheIndex < binding.CacheEntries.Count; cacheIndex++)
                {
                    BakedCacheEntry entry = binding.CacheEntries[cacheIndex];
                    if (entry != null) entry.RefCount = Mathf.Max(0, entry.RefCount - 1);
                }
            }
            _bakedBindings.Clear();
            for (int i = 0; i < _bakedPaintMaterials.Count; i++)
                if (_bakedPaintMaterials[i] != null) Destroy(_bakedPaintMaterials[i]);
            _bakedPaintMaterials.Clear();
            for (int i = 0; i < _bakedPaintTextures.Count; i++)
                if (_bakedPaintTextures[i] != null) Destroy(_bakedPaintTextures[i]);
            _bakedPaintTextures.Clear();
        }

        private string BuildBakeCacheKey(MeshSource source, Material original, Texture sourceTexture,
            IList<int> submeshes, Vector2[] uvs, Vector3[] partVertices,
            IList<HeatTileLayerState> layers, IList<CoverageContext> contexts,
            Vector2 textureScale, Vector2 textureOffset)
        {
            ulong hash = 1469598103934665603UL;
            HashString(ref hash, "reforged-bake-v2");
            // The cached object is a complete cloned material. Include its exact source identity so
            // a visually similar material cannot import another part's normal/specular properties.
            HashValue(ref hash, original.GetInstanceID());
            HashString(ref hash, original.shader == null ? "" : original.shader.name);
            HashString(ref hash, sourceTexture == null ? "<white>" : sourceTexture.name);
            HashValue(ref hash, sourceTexture == null ? 0 : sourceTexture.GetInstanceID());
            HashValue(ref hash, sourceTexture == null ? UntexturedBakeDimension : sourceTexture.width);
            HashValue(ref hash, sourceTexture == null ? UntexturedBakeDimension : sourceTexture.height);
            HashValue(ref hash, original.renderQueue);
            HashVector2(ref hash, textureScale);
            HashVector2(ref hash, textureOffset);
            if (original.HasProperty("_Color")) HashColor(ref hash, original.GetColor("_Color"));

            HashValue(ref hash, submeshes.Count);
            for (int i = 0; i < submeshes.Count; i++)
            {
                int submesh = submeshes[i];
                HashValue(ref hash, submesh);
                int[] triangles;
                try { triangles = source.Mesh.GetTriangles(submesh); }
                catch (Exception) { triangles = new int[0]; }
                HashValue(ref hash, triangles.Length);
                for (int triangle = 0; triangle < triangles.Length; triangle++)
                    HashValue(ref hash, triangles[triangle]);
            }

            HashValue(ref hash, partVertices.Length);
            for (int i = 0; i < partVertices.Length; i++)
            {
                HashVector(ref hash, partVertices[i]);
                if (i < uvs.Length) HashVector2(ref hash, uvs[i]);
            }

            bool usesCraftSpacePattern = false;
            HashValue(ref hash, layers.Count);
            for (int i = 0; i < layers.Count; i++)
            {
                HeatTileLayerState layer = layers[i];
                CoverageContext context = contexts[i];
                HashString(ref hash, SerializeLayer(layer));
                HashVector(ref hash, context.DirectionA);
                HashVector(ref hash, context.DirectionB);
                HashFloat(ref hash, context.ClipA);
                HashFloat(ref hash, context.ClipB);
                HashFloat(ref hash, context.BlendA);
                HashFloat(ref hash, context.BlendB);
                HashValue(ref hash, context.BlendBands);
                HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
                if (definition != null && !definition.IsFlatColor) usesCraftSpacePattern = true;
            }

            // Pattern phase is craft-space. Flat paint is deliberately independent of the part's
            // placement so symmetry counterparts and editor/flight rebuilds can share one result.
            if (usesCraftSpacePattern)
            {
                Matrix4x4 partToCraft = RelativeMatrix(part.transform, GetCraftMappingTransform());
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 4; column++)
                        HashFloat(ref hash, partToCraft[row, column]);
            }
            return hash.ToString("X16", CultureInfo.InvariantCulture);
        }

        private static void HashString(ref ulong hash, string value)
        {
            if (value == null) value = "";
            HashValue(ref hash, value.Length);
            for (int i = 0; i < value.Length; i++) HashValue(ref hash, value[i]);
        }

        private static void HashVector2(ref ulong hash, Vector2 value)
        {
            HashFloat(ref hash, value.x);
            HashFloat(ref hash, value.y);
        }

        private static void HashColor(ref ulong hash, Color value)
        {
            HashFloat(ref hash, value.r);
            HashFloat(ref hash, value.g);
            HashFloat(ref hash, value.b);
            HashFloat(ref hash, value.a);
        }

        private static void EvictUnusedBakeIfNeeded(long incomingBytes)
        {
            while (BakedMaterialCache.Count >= MaximumCachedBakes
                || _bakedCacheBytes + incomingBytes > MaximumBakedCacheBytes)
            {
                BakedCacheEntry oldest = null;
                foreach (KeyValuePair<string, BakedCacheEntry> pair in BakedMaterialCache)
                {
                    BakedCacheEntry candidate = pair.Value;
                    if (candidate == null || candidate.RefCount != 0) continue;
                    if (oldest == null || candidate.LastUsedFrame < oldest.LastUsedFrame)
                        oldest = candidate;
                }
                if (oldest == null) return;
                BakedMaterialCache.Remove(oldest.Key);
                _bakedCacheBytes = Math.Max(0L, _bakedCacheBytes - oldest.EstimatedBytes);
                if (oldest.Material != null) UnityEngine.Object.Destroy(oldest.Material);
                if (oldest.Texture != null) UnityEngine.Object.Destroy(oldest.Texture);
                if (oldest.NormalTexture != null) UnityEngine.Object.Destroy(oldest.NormalTexture);
            }
        }

        private static bool SameMaterialArray(Material[] a, Material[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
