using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace HeatTileOverlayPrototype
{
    // Reforged-derived persistent path. Stable source geometry is clipped into a bounded number of
    // material regions, then the ordered layer stack is composited once into cached albedo/normal
    // material variants. No overlay renderer or per-camera callback survives into flight.
    //
    // Material/cache architecture adapted from Reforged Materials Redux by J4COB (MIT).
    public sealed partial class ModuleHeatTileOverlay
    {
        private const int MaximumFlatRegionLayers = 8;
        private const int MaximumFlatRegionsPerTriangle = 64;
        private const int MaximumFlatRegionSlots = 32;
        private const int MaximumFlatRegionVertices = 250000;
        private const int MaximumFlatRegionIndices = 750000;

        private sealed class FlatRegionBinding
        {
            public MeshFilter Filter;
            public Mesh OriginalMesh;
            public Mesh AppliedMesh;
            public MeshRenderer Renderer;
            public Material[] OriginalMaterials;
            public Material[] AppliedMaterials;
            public readonly List<BakedCacheEntry> CacheEntries = new List<BakedCacheEntry>();
        }

        private struct FlatRegionVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector4 Tangent;
            public Vector2 Uv;
            public Vector2 Uv2;
            public Color32 Color;
            public Vector3 PartPosition;
            public Vector3 PartNormal;
            public int SourceIndex;
        }

        private sealed class FlatRegionPolygon
        {
            public List<FlatRegionVertex> Vertices;
            public int Mask;
        }

        private sealed class FlatRegionSlot
        {
            public int MaterialIndex;
            public int Mask;
            public readonly List<int> Triangles = new List<int>();
        }

        private sealed class FlatRegionMeshData
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<Vector2> Uv2s = new List<Vector2>();
            public readonly List<Color32> Colors = new List<Color32>();
            public readonly List<FlatRegionSlot> Slots = new List<FlatRegionSlot>();
            public readonly Dictionary<long, FlatRegionSlot> SlotsByKey =
                new Dictionary<long, FlatRegionSlot>();
            public readonly Dictionary<int, int> OriginalVertexMap = new Dictionary<int, int>();
            public bool HasTangents;
            public bool HasUv;
            public bool HasUv2;
            public bool HasColors;
            public int IndexCount;
        }

        private struct FlatUvTriangleKey : IEquatable<FlatUvTriangleKey>
        {
            public int MaterialIndex;
            public int Ax;
            public int Ay;
            public int Bx;
            public int By;
            public int Cx;
            public int Cy;

            public bool Equals(FlatUvTriangleKey other)
            {
                return MaterialIndex == other.MaterialIndex
                    && Ax == other.Ax && Ay == other.Ay
                    && Bx == other.Bx && By == other.By
                    && Cx == other.Cx && Cy == other.Cy;
            }

            public override bool Equals(object value)
            {
                return value is FlatUvTriangleKey && Equals((FlatUvTriangleKey)value);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = MaterialIndex;
                    hash = hash * 397 ^ Ax; hash = hash * 397 ^ Ay;
                    hash = hash * 397 ^ Bx; hash = hash * 397 ^ By;
                    hash = hash * 397 ^ Cx; hash = hash * 397 ^ Cy;
                    return hash;
                }
            }
        }

        private struct FlatUvSample
        {
            public int X;
            public int Y;
            public Vector3 PartPosition;
            public Vector3 PartNormal;
        }

        private readonly List<FlatRegionBinding> _flatRegionBindings =
            new List<FlatRegionBinding>();

        private bool ShouldUseFlatRegionPaint(IList<HeatTileLayerState> layers)
        {
            if (layers == null || layers.Count == 0 || layers.Count > MaximumFlatRegionLayers)
                return false;
            // B9 Procedural Wings edit their render mesh in place and retain topology ownership.
            // Other procedural parts replace their mesh as a complete generated snapshot; those
            // snapshots can use bounded regions because source monitoring restores/discards them
            // before the generator's next shape is painted.
            return !IsB9ProceduralPart();
        }

        private static bool AreHardFlatLayers(IList<HeatTileLayerState> layers)
        {
            if (layers == null || layers.Count == 0 || layers.Count > MaximumFlatRegionLayers)
                return false;
            for (int i = 0; i < layers.Count; i++)
            {
                HeatTileLayerState layer = layers[i];
                HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
                if (definition == null || layer.EdgeStyle != 0) return false;
            }
            return true;
        }

        // A material group that is wholly covered by one layer mask needs neither UV rasterization
        // nor a split mesh. This is especially valuable for B9's separate top/bottom/edge renderers.
        private static bool TryGetUniformFlatMask(MeshSource source, IList<int> submeshes,
            Vector3[] partVertices, Vector3[] partNormals, bool hasNormals,
            IList<CoverageContext> contexts, out int uniformMask)
        {
            uniformMask = 0;
            bool foundTriangle = false;
            bool maskInitialized = false;
            for (int groupIndex = 0; groupIndex < submeshes.Count; groupIndex++)
            {
                int[] triangles;
                try { triangles = source.Mesh.GetTriangles(submeshes[groupIndex]); }
                catch (Exception) { return false; }
                for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                {
                    int ia = triangles[triangle];
                    int ib = triangles[triangle + 1];
                    int ic = triangles[triangle + 2];
                    if (ia < 0 || ib < 0 || ic < 0 || ia >= partVertices.Length
                        || ib >= partVertices.Length || ic >= partVertices.Length) continue;
                    Vector3 faceNormal = Vector3.Cross(partVertices[ib] - partVertices[ia],
                        partVertices[ic] - partVertices[ia]).normalized;
                    FlatUvSample a = new FlatUvSample
                    {
                        PartPosition = partVertices[ia],
                        PartNormal = hasNormals ? partNormals[ia] : faceNormal
                    };
                    FlatUvSample b = new FlatUvSample
                    {
                        PartPosition = partVertices[ib],
                        PartNormal = hasNormals ? partNormals[ib] : faceNormal
                    };
                    FlatUvSample c = new FlatUvSample
                    {
                        PartPosition = partVertices[ic],
                        PartNormal = hasNormals ? partNormals[ic] : faceNormal
                    };
                    int mask = 0;
                    for (int layerIndex = 0; layerIndex < contexts.Count; layerIndex++)
                    {
                        int classification = ClassifyFlatTriangle(a, b, c, contexts[layerIndex]);
                        if (classification == 0) return false;
                        if (classification > 0) mask |= 1 << layerIndex;
                    }
                    foundTriangle = true;
                    if (!maskInitialized)
                    {
                        uniformMask = mask;
                        maskInitialized = true;
                    }
                    else if (uniformMask != mask) return false;
                }
            }
            return foundTriangle;
        }

        // The conventional path changes only the source albedo and therefore adds no draw calls.
        // It is correct unless two geometric surfaces reuse the same UV triangle but require
        // different coverage or different craft-space pattern samples. Detect that specific
        // conflict and reserve geometric isolation for it.
        private bool NeedsSharedUvPaintIsolation(IList<MeshSource> sources,
            IList<CoverageContext> contexts, IList<HeatTileLayerState> layers)
        {
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                MeshSource source = sources[sourceIndex];
                if (source == null || source.Mesh == null || source.Renderer == null) continue;
                Vector3[] vertices;
                Vector3[] normals;
                Vector2[] uvs;
                try
                {
                    vertices = source.Mesh.vertices;
                    normals = source.Mesh.normals;
                    uvs = source.Mesh.uv;
                }
                catch (Exception)
                {
                    // A non-readable mesh cannot use the albedo rasterizer but can still be split
                    // by Unity only if its channels become readable later; let that path decide.
                    return true;
                }
                if (vertices == null || vertices.Length == 0
                    || uvs == null || uvs.Length != vertices.Length) return true;
                bool hasNormals = normals != null && normals.Length == vertices.Length;
                Matrix4x4 normalToPart = source.SourceToPart.inverse.transpose;
                Material[] materials = source.Renderer.sharedMaterials;
                if (materials == null || materials.Length == 0) continue;
                Dictionary<FlatUvTriangleKey, ulong> signatures =
                    new Dictionary<FlatUvTriangleKey, ulong>();
                for (int submesh = 0; submesh < source.Mesh.subMeshCount; submesh++)
                {
                    if (!IsEligibleSubmesh(source, submesh)) continue;
                    int materialIndex = Mathf.Min(submesh, materials.Length - 1);
                    Material material = materials[materialIndex];
                    Vector2 scale = material != null && material.HasProperty("_MainTex")
                        ? material.GetTextureScale("_MainTex") : Vector2.one;
                    Vector2 offset = material != null && material.HasProperty("_MainTex")
                        ? material.GetTextureOffset("_MainTex") : Vector2.zero;
                    Texture texture = material != null && material.HasProperty("_MainTex")
                        ? material.GetTexture("_MainTex") : null;
                    bool repeat = texture == null || texture.wrapMode == TextureWrapMode.Repeat;
                    int[] triangles;
                    try { triangles = source.Mesh.GetTriangles(submesh); }
                    catch (Exception) { return true; }
                    for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                    {
                        int ia = triangles[triangle];
                        int ib = triangles[triangle + 1];
                        int ic = triangles[triangle + 2];
                        if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Length
                            || ib >= vertices.Length || ic >= vertices.Length) continue;
                        Vector3 faceNormal = Vector3.Cross(vertices[ib] - vertices[ia],
                            vertices[ic] - vertices[ia]).normalized;
                        FlatUvSample a = CreateFlatUvSample(ia, vertices, normals, uvs,
                            hasNormals, faceNormal, source.SourceToPart, normalToPart,
                            scale, offset, repeat);
                        FlatUvSample b = CreateFlatUvSample(ib, vertices, normals, uvs,
                            hasNormals, faceNormal, source.SourceToPart, normalToPart,
                            scale, offset, repeat);
                        FlatUvSample c = CreateFlatUvSample(ic, vertices, normals, uvs,
                            hasNormals, faceNormal, source.SourceToPart, normalToPart,
                            scale, offset, repeat);
                        SortFlatUvSamples(ref a, ref b, ref c);
                        FlatUvTriangleKey key = new FlatUvTriangleKey
                        {
                            MaterialIndex = materialIndex,
                            Ax = a.X, Ay = a.Y,
                            Bx = b.X, By = b.Y,
                            Cx = c.X, Cy = c.Y
                        };
                        ulong signature = BuildSharedUvPaintSignature(a, b, c, contexts, layers);
                        ulong prior;
                        if (signatures.TryGetValue(key, out prior))
                        {
                            if (prior != signature) return true;
                        }
                        else signatures.Add(key, signature);
                    }
                }
            }
            return false;
        }

        private static FlatUvSample CreateFlatUvSample(int index, Vector3[] vertices,
            Vector3[] normals, Vector2[] uvs, bool hasNormals, Vector3 faceNormal,
            Matrix4x4 sourceToPart, Matrix4x4 normalToPart, Vector2 scale,
            Vector2 offset, bool repeat)
        {
            Vector2 uv = TransformUv(uvs[index], scale, offset);
            if (repeat)
            {
                uv.x = Mathf.Repeat(uv.x, 1f);
                uv.y = Mathf.Repeat(uv.y, 1f);
            }
            Vector3 normal = hasNormals ? normals[index] : faceNormal;
            return new FlatUvSample
            {
                X = Mathf.RoundToInt(uv.x * 100000f),
                Y = Mathf.RoundToInt(uv.y * 100000f),
                PartPosition = sourceToPart.MultiplyPoint3x4(vertices[index]),
                PartNormal = normalToPart.MultiplyVector(normal).normalized
            };
        }

        private static void SortFlatUvSamples(ref FlatUvSample a, ref FlatUvSample b,
            ref FlatUvSample c)
        {
            if (CompareFlatUvSamples(a, b) > 0) SwapFlatUvSamples(ref a, ref b);
            if (CompareFlatUvSamples(b, c) > 0) SwapFlatUvSamples(ref b, ref c);
            if (CompareFlatUvSamples(a, b) > 0) SwapFlatUvSamples(ref a, ref b);
        }

        private static int CompareFlatUvSamples(FlatUvSample a, FlatUvSample b)
        {
            int x = a.X.CompareTo(b.X);
            return x != 0 ? x : a.Y.CompareTo(b.Y);
        }

        private static void SwapFlatUvSamples(ref FlatUvSample a, ref FlatUvSample b)
        {
            FlatUvSample temporary = a;
            a = b;
            b = temporary;
        }

        private ulong BuildSharedUvPaintSignature(FlatUvSample a, FlatUvSample b,
            FlatUvSample c, IList<CoverageContext> contexts,
            IList<HeatTileLayerState> layers)
        {
            ulong hash = 1469598103934665603UL;
            HashValue(ref hash, contexts.Count);
            for (int i = 0; i < contexts.Count; i++)
            {
                CoverageContext context = contexts[i];
                int classification = ClassifyFlatTriangle(a, b, c, context);
                HashValue(ref hash, classification);
                if (classification == 0)
                {
                    HashFlatCoverageMargins(ref hash, a, context);
                    HashFlatCoverageMargins(ref hash, b, context);
                    HashFlatCoverageMargins(ref hash, c, context);
                }
                if (classification < 0 || layers == null || i >= layers.Count) continue;
                HeatTileTextureDefinition definition =
                    HeatTileTextureLibrary.Find(layers[i].TextureId);
                if (definition == null || definition.IsFlatColor) continue;
                float span = Mathf.Max(0.1f, definition.PatternSpan);
                HashPatternProjection(ref hash, a, span);
                HashPatternProjection(ref hash, b, span);
                HashPatternProjection(ref hash, c, span);
            }
            return hash;
        }

        private void HashPatternProjection(ref ulong hash, FlatUvSample sample, float span)
        {
            Vector2 uv = CalculateUv(sample.PartPosition, sample.PartNormal, span);
            // Pattern textures repeat, so positions separated by a whole pattern span are
            // genuinely equivalent and should not trigger the more expensive fallback.
            HashFloat(ref hash, Mathf.Repeat(uv.x, 1f));
            HashFloat(ref hash, Mathf.Repeat(uv.y, 1f));
        }

        private static int ClassifyFlatTriangle(FlatUvSample a, FlatUvSample b,
            FlatUvSample c, CoverageContext context)
        {
            int first = ClassifyFlatTriangleDirection(a, b, c, context.DirectionA,
                context.ClipA, context.State.DirectionFacingOnly, context.State.SurfaceWrap);
            if (!context.Mirrored) return first;
            int second = ClassifyFlatTriangleDirection(a, b, c, context.DirectionB,
                context.ClipB, context.State.DirectionFacingOnly, context.State.SurfaceWrap);
            if (context.State.MirrorMode == 1)
            {
                if (first > 0 || second > 0) return 1;
                if (first < 0 && second < 0) return -1;
                return 0;
            }
            if (first < 0 || second < 0) return -1;
            if (first > 0 && second > 0) return 1;
            return 0;
        }

        private static int ClassifyFlatTriangleDirection(FlatUvSample a, FlatUvSample b,
            FlatUvSample c, Vector3 direction, float clip, bool facingOnly, float surfaceWrap)
        {
            int position = ClassifyThreeValues(Vector3.Dot(a.PartPosition, direction) - clip,
                Vector3.Dot(b.PartPosition, direction) - clip,
                Vector3.Dot(c.PartPosition, direction) - clip);
            if (position <= 0) return position;
            if (!facingOnly) return 1;
            float threshold = Mathf.Cos(surfaceWrap * Mathf.Deg2Rad);
            return ClassifyThreeValues(Vector3.Dot(a.PartNormal, direction) - threshold,
                Vector3.Dot(b.PartNormal, direction) - threshold,
                Vector3.Dot(c.PartNormal, direction) - threshold);
        }

        private static int ClassifyThreeValues(float a, float b, float c)
        {
            bool anyInside = a >= -0.000001f || b >= -0.000001f || c >= -0.000001f;
            bool anyOutside = a < -0.000001f || b < -0.000001f || c < -0.000001f;
            if (anyInside && anyOutside) return 0;
            return anyInside ? 1 : -1;
        }

        private static void HashFlatCoverageMargins(ref ulong hash, FlatUvSample sample,
            CoverageContext context)
        {
            HashFloat(ref hash, Vector3.Dot(sample.PartPosition, context.DirectionA) - context.ClipA);
            if (context.State.DirectionFacingOnly)
                HashFloat(ref hash, Vector3.Dot(sample.PartNormal, context.DirectionA)
                    - Mathf.Cos(context.State.SurfaceWrap * Mathf.Deg2Rad));
            if (!context.Mirrored) return;
            HashFloat(ref hash, Vector3.Dot(sample.PartPosition, context.DirectionB) - context.ClipB);
            if (context.State.DirectionFacingOnly)
                HashFloat(ref hash, Vector3.Dot(sample.PartNormal, context.DirectionB)
                    - Mathf.Cos(context.State.SurfaceWrap * Mathf.Deg2Rad));
        }

        private bool TryRebuildUniformSurfacePaint(IList<MeshSource> sources,
            IList<HeatTileLayerState> layers, IList<CoverageContext> contexts)
        {
            if (!AreHardFlatLayers(layers)) return false;
            List<BakedMaterialBinding> pending = new List<BakedMaterialBinding>();
            bool paintedAny = false;
            try
            {
                for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                {
                    MeshSource source = sources[sourceIndex];
                    if (source == null || source.Mesh == null || source.Renderer == null) continue;
                    Vector3[] vertices = source.Mesh.vertices;
                    Vector3[] normals = source.Mesh.normals;
                    bool hasNormals = normals != null && normals.Length == vertices.Length;
                    Vector3[] partVertices = new Vector3[vertices.Length];
                    Vector3[] partNormals = new Vector3[vertices.Length];
                    Matrix4x4 normalToPart = source.SourceToPart.inverse.transpose;
                    for (int vertex = 0; vertex < vertices.Length; vertex++)
                    {
                        partVertices[vertex] = source.SourceToPart.MultiplyPoint3x4(vertices[vertex]);
                        partNormals[vertex] = hasNormals
                            ? normalToPart.MultiplyVector(normals[vertex]).normalized : Vector3.zero;
                    }

                    Material[] originals = source.Renderer.sharedMaterials;
                    if (originals == null || originals.Length == 0) continue;
                    Material[] applied = new Material[originals.Length];
                    Array.Copy(originals, applied, originals.Length);
                    BakedMaterialBinding binding = new BakedMaterialBinding
                    {
                        Renderer = source.Renderer,
                        Original = originals,
                        Applied = applied
                    };
                    pending.Add(binding);

                    Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
                    for (int submesh = 0; submesh < source.Mesh.subMeshCount; submesh++)
                    {
                        if (!IsEligibleSubmesh(source, submesh)) continue;
                        int materialIndex = Mathf.Min(submesh, originals.Length - 1);
                        List<int> group;
                        if (!groups.TryGetValue(materialIndex, out group))
                        {
                            group = new List<int>();
                            groups.Add(materialIndex, group);
                        }
                        group.Add(submesh);
                    }

                    foreach (KeyValuePair<int, List<int>> pair in groups)
                    {
                        int mask;
                        if (!TryGetUniformFlatMask(source, pair.Value, partVertices, partNormals,
                            hasNormals, contexts, out mask))
                        {
                            ReleasePendingUniformBindings(pending);
                            return false;
                        }
                        if (mask == 0) continue;
                        Material painted;
                        BakedCacheEntry entry;
                        bool cacheHit;
                        if (!TryGetFlatRegionMaterial(source, originals[pair.Key], layers, mask,
                            out painted, out entry, out cacheHit))
                        {
                            ReleasePendingUniformBindings(pending);
                            return false;
                        }
                        applied[pair.Key] = painted;
                        entry.RefCount++;
                        entry.LastUsedFrame = Time.frameCount;
                        binding.CacheEntries.Add(entry);
                        paintedAny = true;
                    }
                }

                if (!paintedAny)
                {
                    ReleasePendingUniformBindings(pending);
                    return false;
                }
                for (int i = 0; i < pending.Count; i++)
                {
                    BakedMaterialBinding binding = pending[i];
                    if (binding.CacheEntries.Count == 0) continue;
                    binding.Renderer.sharedMaterials = binding.Applied;
                    _bakedBindings.Add(binding);
                }
                Debug.Log("[HeatTilePainter] Reforged-style uniform material paint on "
                    + part.partInfo.name + ": sources=" + pending.Count
                    + ", persistentOverlayRenderers=0, meshReplacements=0.");
                return true;
            }
            catch (Exception exception)
            {
                ReleasePendingUniformBindings(pending);
                Debug.LogWarning("[HeatTilePainter] Uniform material adapter declined "
                    + part.partInfo.name + ": " + exception.Message);
                return false;
            }
        }

        private static void ReleasePendingUniformBindings(IList<BakedMaterialBinding> pending)
        {
            if (pending == null) return;
            for (int i = 0; i < pending.Count; i++)
            {
                BakedMaterialBinding binding = pending[i];
                if (binding == null) continue;
                for (int entryIndex = 0; entryIndex < binding.CacheEntries.Count; entryIndex++)
                {
                    BakedCacheEntry entry = binding.CacheEntries[entryIndex];
                    if (entry != null) entry.RefCount = Mathf.Max(0, entry.RefCount - 1);
                }
            }
        }

        private bool TryRebuildFlatRegionPaint(IList<MeshSource> sources,
            IList<HeatTileLayerState> layers, IList<CoverageContext> contexts)
        {
            DateTime started = DateTime.UtcNow;
            int paintedSources = 0;
            int originalSubmeshes = 0;
            int appliedSubmeshes = 0;
            int materialCacheHits = 0;
            try
            {
                for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                {
                    MeshSource source = sources[sourceIndex];
                    if (source == null || source.Mesh == null || source.Filter == null
                        || source.Renderer == null) continue;
                    FlatRegionBinding binding;
                    int sourceCacheHits;
                    if (!TryBuildFlatRegionSource(source, layers, contexts,
                        out binding, out sourceCacheHits))
                    {
                        RestoreFlatRegionPaint();
                        return false;
                    }
                    materialCacheHits += sourceCacheHits;
                    if (binding == null) continue;
                    _flatRegionBindings.Add(binding);
                    paintedSources++;
                    originalSubmeshes += binding.OriginalMesh == null
                        ? 0 : binding.OriginalMesh.subMeshCount;
                    appliedSubmeshes += binding.AppliedMesh == null
                        ? 0 : binding.AppliedMesh.subMeshCount;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[HeatTilePainter] Flat-region split failed on "
                    + part.partInfo.name + ": " + exception.Message
                    + "; returning to the conventional texture bake.");
                RestoreFlatRegionPaint();
                return false;
            }

            if (paintedSources == 0)
            {
                RestoreFlatRegionPaint();
                return false;
            }
            Debug.Log("[HeatTilePainter] Flat-region painted " + layers.Count
                + " layer(s) across " + paintedSources + " source(s) on " + part.partInfo.name
                + ": originalSubmeshes=" + originalSubmeshes
                + ", appliedSubmeshes=" + appliedSubmeshes
                + ", additionalDrawRegions=" + Math.Max(0, appliedSubmeshes - originalSubmeshes)
                + ", materialCacheHits=" + materialCacheHits
                + ", milliseconds=" + (DateTime.UtcNow - started).TotalMilliseconds.ToString("F1",
                    CultureInfo.InvariantCulture)
                + ", atlases=0, persistentOverlayRenderers=0.");
            return true;
        }

        private bool TryBuildFlatRegionSource(MeshSource source,
            IList<HeatTileLayerState> layers, IList<CoverageContext> contexts,
            out FlatRegionBinding binding, out int cacheHits)
        {
            binding = null;
            cacheHits = 0;
            Mesh appliedMesh = null;
            List<BakedCacheEntry> retainedEntries = new List<BakedCacheEntry>();
            try
            {
                Vector3[] vertices = source.Mesh.vertices;
                Vector3[] normals = source.Mesh.normals;
                Vector4[] tangents = source.Mesh.tangents;
                Vector2[] uvs = source.Mesh.uv;
                Vector2[] uv2s = source.Mesh.uv2;
                Color32[] colors = source.Mesh.colors32;
                if (vertices == null || vertices.Length == 0) return false;
                bool hasNormals = normals != null && normals.Length == vertices.Length;
                bool hasTangents = tangents != null && tangents.Length == vertices.Length;
                bool hasUv = uvs != null && uvs.Length == vertices.Length;
                bool hasUv2 = uv2s != null && uv2s.Length == vertices.Length;
                bool hasColors = colors != null && colors.Length == vertices.Length;
                Matrix4x4 normalToPart = source.SourceToPart.inverse.transpose;

                Material[] originals = source.Renderer.sharedMaterials;
                if (originals == null || originals.Length == 0) return false;
                FlatRegionMeshData data = new FlatRegionMeshData
                {
                    HasTangents = hasTangents,
                    HasUv = hasUv,
                    HasUv2 = hasUv2,
                    HasColors = hasColors
                };

                for (int submesh = 0; submesh < source.Mesh.subMeshCount; submesh++)
                {
                    int materialIndex = Mathf.Min(submesh, originals.Length - 1);
                    bool eligible = IsEligibleSubmesh(source, submesh);
                    int[] triangles = source.Mesh.GetTriangles(submesh);
                    for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                    {
                        int ia = triangles[triangle];
                        int ib = triangles[triangle + 1];
                        int ic = triangles[triangle + 2];
                        if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Length
                            || ib >= vertices.Length || ic >= vertices.Length) continue;
                        Vector3 faceNormal = Vector3.Cross(vertices[ib] - vertices[ia],
                            vertices[ic] - vertices[ia]).normalized;
                        FlatRegionVertex a = CreateFlatRegionVertex(ia, vertices, normals,
                            tangents, uvs, uv2s, colors, hasNormals, hasTangents, hasUv,
                            hasUv2, hasColors, faceNormal, source.SourceToPart, normalToPart);
                        FlatRegionVertex b = CreateFlatRegionVertex(ib, vertices, normals,
                            tangents, uvs, uv2s, colors, hasNormals, hasTangents, hasUv,
                            hasUv2, hasColors, faceNormal, source.SourceToPart, normalToPart);
                        FlatRegionVertex c = CreateFlatRegionVertex(ic, vertices, normals,
                            tangents, uvs, uv2s, colors, hasNormals, hasTangents, hasUv,
                            hasUv2, hasColors, faceNormal, source.SourceToPart, normalToPart);
                        List<FlatRegionPolygon> regions = new List<FlatRegionPolygon>
                        {
                            new FlatRegionPolygon
                            {
                                Vertices = new List<FlatRegionVertex> { a, b, c },
                                Mask = 0
                            }
                        };
                        if (eligible)
                            for (int layer = 0; layer < layers.Count; layer++)
                            {
                                regions = PartitionFlatRegions(regions, contexts[layer], 1 << layer);
                                if (regions.Count > MaximumFlatRegionsPerTriangle)
                                    throw new InvalidOperationException("layer partition exceeded the per-triangle safety limit");
                            }
                        for (int region = 0; region < regions.Count; region++)
                        {
                            AddFlatRegionPolygon(regions[region], materialIndex, data);
                            if (data.Slots.Count > MaximumFlatRegionSlots
                                || data.Vertices.Count > MaximumFlatRegionVertices
                                || data.IndexCount > MaximumFlatRegionIndices)
                                throw new InvalidOperationException("generated paint regions exceeded the mesh safety budget");
                        }
                    }
                }

                bool hasPaintedRegion = false;
                for (int i = 0; i < data.Slots.Count; i++)
                    if (data.Slots[i].Mask != 0 && data.Slots[i].Triangles.Count > 0)
                    {
                        hasPaintedRegion = true;
                        break;
                    }
                if (!hasPaintedRegion) return true;

                Material[] appliedMaterials = new Material[data.Slots.Count];
                for (int slotIndex = 0; slotIndex < data.Slots.Count; slotIndex++)
                {
                    FlatRegionSlot slot = data.Slots[slotIndex];
                    Material original = originals[Mathf.Min(slot.MaterialIndex, originals.Length - 1)];
                    if (slot.Mask == 0)
                    {
                        appliedMaterials[slotIndex] = original;
                        continue;
                    }
                    Material painted;
                    BakedCacheEntry entry;
                    bool cacheHit;
                    if (!TryGetFlatRegionMaterial(source, original, layers, slot.Mask,
                        out painted, out entry, out cacheHit)) return false;
                    appliedMaterials[slotIndex] = painted;
                    entry.RefCount++;
                    entry.LastUsedFrame = Time.frameCount;
                    retainedEntries.Add(entry);
                    if (cacheHit) cacheHits++;
                }

                appliedMesh = new Mesh();
                appliedMesh.name = source.Mesh.name + "-HeatTilePainter-FlatRegions";
                if (data.Vertices.Count > 65000) appliedMesh.indexFormat = IndexFormat.UInt32;
                appliedMesh.SetVertices(data.Vertices);
                appliedMesh.SetNormals(data.Normals);
                if (data.HasTangents) appliedMesh.SetTangents(data.Tangents);
                if (data.HasUv) appliedMesh.SetUVs(0, data.Uvs);
                if (data.HasUv2) appliedMesh.SetUVs(1, data.Uv2s);
                if (data.HasColors) appliedMesh.SetColors(data.Colors);
                appliedMesh.subMeshCount = data.Slots.Count;
                for (int slotIndex = 0; slotIndex < data.Slots.Count; slotIndex++)
                    appliedMesh.SetTriangles(data.Slots[slotIndex].Triangles, slotIndex, false);
                appliedMesh.bounds = source.Mesh.bounds;

                FlatRegionBinding result = new FlatRegionBinding
                {
                    Filter = source.Filter,
                    OriginalMesh = source.Filter.sharedMesh,
                    AppliedMesh = appliedMesh,
                    Renderer = source.Renderer,
                    OriginalMaterials = originals,
                    AppliedMaterials = appliedMaterials
                };
                result.CacheEntries.AddRange(retainedEntries);
                try
                {
                    source.Filter.sharedMesh = appliedMesh;
                    source.Renderer.sharedMaterials = appliedMaterials;
                }
                catch (Exception)
                {
                    RestoreFlatRegionBindingState(result);
                    throw;
                }
                binding = result;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[HeatTilePainter] Could not split source " + source.Key
                    + " on " + part.partInfo.name + ": " + exception.Message);
                return false;
            }
            finally
            {
                if (binding == null)
                {
                    for (int i = 0; i < retainedEntries.Count; i++)
                        retainedEntries[i].RefCount = Mathf.Max(0, retainedEntries[i].RefCount - 1);
                    if (appliedMesh != null) Destroy(appliedMesh);
                }
            }
        }

        private static FlatRegionVertex CreateFlatRegionVertex(int index, Vector3[] vertices,
            Vector3[] normals, Vector4[] tangents, Vector2[] uvs, Vector2[] uv2s, Color32[] colors,
            bool hasNormals, bool hasTangents, bool hasUv, bool hasUv2, bool hasColors,
            Vector3 faceNormal, Matrix4x4 sourceToPart, Matrix4x4 normalToPart)
        {
            Vector3 normal = hasNormals ? normals[index] : faceNormal;
            return new FlatRegionVertex
            {
                Position = vertices[index],
                Normal = normal,
                Tangent = hasTangents ? tangents[index] : new Vector4(1f, 0f, 0f, 1f),
                Uv = hasUv ? uvs[index] : Vector2.zero,
                Uv2 = hasUv2 ? uv2s[index] : Vector2.zero,
                Color = hasColors ? colors[index] : new Color32(255, 255, 255, 255),
                PartPosition = sourceToPart.MultiplyPoint3x4(vertices[index]),
                PartNormal = normalToPart.MultiplyVector(normal).normalized,
                // A mesh without authored normals needs one copy per face; reusing its shared
                // source index would accidentally smooth hard edges with the first triangle's normal.
                SourceIndex = hasNormals ? index : -1
            };
        }

        private static List<FlatRegionPolygon> PartitionFlatRegions(
            List<FlatRegionPolygon> input, CoverageContext context, int layerBit)
        {
            List<FlatRegionPolygon> result = new List<FlatRegionPolygon>();
            for (int i = 0; i < input.Count; i++)
            {
                FlatRegionPolygon region = input[i];
                int classification = ClassifyFlatCoverage(region.Vertices, context);
                if (classification > 0)
                {
                    region.Mask |= layerBit;
                    result.Add(region);
                    continue;
                }
                if (classification < 0)
                {
                    result.Add(region);
                    continue;
                }
                List<List<FlatRegionVertex>> inside;
                List<List<FlatRegionVertex>> outside;
                PartitionFlatCoverage(region.Vertices, context, out inside, out outside);
                for (int polygon = 0; polygon < outside.Count; polygon++)
                    result.Add(new FlatRegionPolygon
                    {
                        Vertices = outside[polygon],
                        Mask = region.Mask
                    });
                for (int polygon = 0; polygon < inside.Count; polygon++)
                    result.Add(new FlatRegionPolygon
                    {
                        Vertices = inside[polygon],
                        Mask = region.Mask | layerBit
                    });
            }
            return result;
        }

        // Returns 1 when the entire polygon is covered, -1 when none of it can be covered, and 0
        // when clipping is required. The mirror cases deliberately use conservative half-space
        // rules: an intersection may cross the polygon even when no original vertex is inside it.
        private static int ClassifyFlatCoverage(List<FlatRegionVertex> polygon,
            CoverageContext context)
        {
            int a = ClassifyFlatDirection(polygon, context.DirectionA, context.ClipA,
                context.State.DirectionFacingOnly, context.State.SurfaceWrap);
            if (!context.Mirrored) return a;
            int b = ClassifyFlatDirection(polygon, context.DirectionB, context.ClipB,
                context.State.DirectionFacingOnly, context.State.SurfaceWrap);
            if (context.State.MirrorMode == 1)
            {
                if (a > 0 || b > 0) return 1;
                if (a < 0 && b < 0) return -1;
                return 0;
            }
            if (a < 0 || b < 0) return -1;
            if (a > 0 && b > 0) return 1;
            return 0;
        }

        private static int ClassifyFlatDirection(List<FlatRegionVertex> polygon,
            Vector3 direction, float clip, bool facingOnly, float surfaceWrap)
        {
            int position = ClassifyFlatRegionPlane(polygon, direction, clip, false);
            if (position <= 0) return position;
            if (!facingOnly) return 1;
            float threshold = Mathf.Cos(surfaceWrap * Mathf.Deg2Rad);
            return ClassifyFlatRegionPlane(polygon, direction, threshold, true);
        }

        private static int ClassifyFlatRegionPlane(List<FlatRegionVertex> polygon,
            Vector3 direction, float threshold, bool normalPlane)
        {
            if (polygon == null || polygon.Count == 0) return -1;
            bool anyInside = false;
            bool anyOutside = false;
            for (int i = 0; i < polygon.Count; i++)
            {
                float value = FlatRegionPlaneValue(polygon[i], direction, normalPlane) - threshold;
                if (value >= -0.000001f) anyInside = true; else anyOutside = true;
                if (anyInside && anyOutside) return 0;
            }
            return anyInside ? 1 : -1;
        }

        private static void PartitionFlatCoverage(List<FlatRegionVertex> polygon,
            CoverageContext context, out List<List<FlatRegionVertex>> inside,
            out List<List<FlatRegionVertex>> outside)
        {
            List<List<FlatRegionVertex>> insideA;
            List<List<FlatRegionVertex>> outsideA;
            PartitionFlatDirection(polygon, context.DirectionA, context.ClipA,
                context.State.DirectionFacingOnly, context.State.SurfaceWrap,
                out insideA, out outsideA);
            if (!context.Mirrored)
            {
                inside = insideA;
                outside = outsideA;
                return;
            }

            if (context.State.MirrorMode == 1)
            {
                // Union: A plus the portion of B that was outside A.
                inside = new List<List<FlatRegionVertex>>(insideA);
                outside = new List<List<FlatRegionVertex>>();
                for (int i = 0; i < outsideA.Count; i++)
                {
                    List<List<FlatRegionVertex>> insideB;
                    List<List<FlatRegionVertex>> outsideB;
                    PartitionFlatDirection(outsideA[i], context.DirectionB, context.ClipB,
                        context.State.DirectionFacingOnly, context.State.SurfaceWrap,
                        out insideB, out outsideB);
                    inside.AddRange(insideB);
                    outside.AddRange(outsideB);
                }
            }
            else
            {
                // Intersection: only the portion of A that also lies inside B.
                outside = new List<List<FlatRegionVertex>>(outsideA);
                inside = new List<List<FlatRegionVertex>>();
                for (int i = 0; i < insideA.Count; i++)
                {
                    List<List<FlatRegionVertex>> insideB;
                    List<List<FlatRegionVertex>> outsideB;
                    PartitionFlatDirection(insideA[i], context.DirectionB, context.ClipB,
                        context.State.DirectionFacingOnly, context.State.SurfaceWrap,
                        out insideB, out outsideB);
                    inside.AddRange(insideB);
                    outside.AddRange(outsideB);
                }
            }
        }

        private static void PartitionFlatDirection(List<FlatRegionVertex> polygon,
            Vector3 direction, float clip, bool facingOnly, float surfaceWrap,
            out List<List<FlatRegionVertex>> inside,
            out List<List<FlatRegionVertex>> outside)
        {
            inside = new List<List<FlatRegionVertex>>();
            outside = new List<List<FlatRegionVertex>>();
            List<FlatRegionVertex> working;
            List<FlatRegionVertex> rejected;
            SplitFlatRegionPlane(polygon, direction, clip, false, out working, out rejected);
            AddFlatRegionIfValid(outside, rejected);
            if (working == null || working.Count < 3) return;
            if (facingOnly)
            {
                float threshold = Mathf.Cos(surfaceWrap * Mathf.Deg2Rad);
                SplitFlatRegionPlane(working, direction, threshold, true,
                    out working, out rejected);
                AddFlatRegionIfValid(outside, rejected);
            }
            AddFlatRegionIfValid(inside, working);
        }

        private static void SplitFlatRegionPlane(List<FlatRegionVertex> input,
            Vector3 direction, float threshold, bool normalPlane,
            out List<FlatRegionVertex> inside, out List<FlatRegionVertex> outside)
        {
            inside = new List<FlatRegionVertex>();
            outside = new List<FlatRegionVertex>();
            if (input == null || input.Count == 0) return;
            FlatRegionVertex previous = input[input.Count - 1];
            float previousValue = FlatRegionPlaneValue(previous, direction, normalPlane) - threshold;
            bool previousInside = previousValue >= -0.000001f;
            for (int i = 0; i < input.Count; i++)
            {
                FlatRegionVertex current = input[i];
                float currentValue = FlatRegionPlaneValue(current, direction, normalPlane) - threshold;
                bool currentInside = currentValue >= -0.000001f;
                if (currentInside != previousInside)
                {
                    float denominator = currentValue - previousValue;
                    float t = Math.Abs(denominator) < 0.000001f ? 0f
                        : Mathf.Clamp01(-previousValue / denominator);
                    FlatRegionVertex split = LerpFlatRegionVertex(previous, current, t);
                    inside.Add(split);
                    outside.Add(split);
                }
                if (currentInside) inside.Add(current); else outside.Add(current);
                previous = current;
                previousValue = currentValue;
                previousInside = currentInside;
            }
        }

        private static float FlatRegionPlaneValue(FlatRegionVertex vertex,
            Vector3 direction, bool normalPlane)
        {
            return Vector3.Dot(normalPlane ? vertex.PartNormal : vertex.PartPosition, direction);
        }

        private static FlatRegionVertex LerpFlatRegionVertex(FlatRegionVertex a,
            FlatRegionVertex b, float t)
        {
            Vector4 tangent = Vector4.Lerp(a.Tangent, b.Tangent, t);
            Vector3 tangentDirection = new Vector3(tangent.x, tangent.y, tangent.z).normalized;
            tangent.x = tangentDirection.x;
            tangent.y = tangentDirection.y;
            tangent.z = tangentDirection.z;
            return new FlatRegionVertex
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Normal = Vector3.Lerp(a.Normal, b.Normal, t).normalized,
                Tangent = tangent,
                Uv = Vector2.Lerp(a.Uv, b.Uv, t),
                Uv2 = Vector2.Lerp(a.Uv2, b.Uv2, t),
                Color = (Color32)Color.Lerp(a.Color, b.Color, t),
                PartPosition = Vector3.Lerp(a.PartPosition, b.PartPosition, t),
                PartNormal = Vector3.Lerp(a.PartNormal, b.PartNormal, t).normalized,
                SourceIndex = -1
            };
        }

        private static void AddFlatRegionIfValid(List<List<FlatRegionVertex>> destination,
            List<FlatRegionVertex> polygon)
        {
            if (polygon != null && polygon.Count >= 3) destination.Add(polygon);
        }

        private static void AddFlatRegionPolygon(FlatRegionPolygon polygon,
            int materialIndex, FlatRegionMeshData data)
        {
            if (polygon == null || polygon.Vertices == null || polygon.Vertices.Count < 3) return;
            FlatRegionSlot slot = GetFlatRegionSlot(data, materialIndex, polygon.Mask);
            for (int fan = 1; fan + 1 < polygon.Vertices.Count; fan++)
            {
                slot.Triangles.Add(AddFlatRegionVertex(polygon.Vertices[0], data));
                slot.Triangles.Add(AddFlatRegionVertex(polygon.Vertices[fan], data));
                slot.Triangles.Add(AddFlatRegionVertex(polygon.Vertices[fan + 1], data));
                data.IndexCount += 3;
            }
        }

        private static FlatRegionSlot GetFlatRegionSlot(FlatRegionMeshData data,
            int materialIndex, int mask)
        {
            long key = ((long)materialIndex << 32) ^ (uint)mask;
            FlatRegionSlot slot;
            if (data.SlotsByKey.TryGetValue(key, out slot)) return slot;
            slot = new FlatRegionSlot { MaterialIndex = materialIndex, Mask = mask };
            data.SlotsByKey.Add(key, slot);
            data.Slots.Add(slot);
            return slot;
        }

        private static int AddFlatRegionVertex(FlatRegionVertex vertex, FlatRegionMeshData data)
        {
            int existing;
            if (vertex.SourceIndex >= 0
                && data.OriginalVertexMap.TryGetValue(vertex.SourceIndex, out existing))
                return existing;
            int index = data.Vertices.Count;
            data.Vertices.Add(vertex.Position);
            data.Normals.Add(vertex.Normal);
            if (data.HasTangents) data.Tangents.Add(vertex.Tangent);
            if (data.HasUv) data.Uvs.Add(vertex.Uv);
            if (data.HasUv2) data.Uv2s.Add(vertex.Uv2);
            if (data.HasColors) data.Colors.Add(vertex.Color);
            if (vertex.SourceIndex >= 0) data.OriginalVertexMap[vertex.SourceIndex] = index;
            return index;
        }

        private bool TryGetFlatRegionMaterial(MeshSource source, Material original,
            IList<HeatTileLayerState> layers, int mask, out Material material,
            out BakedCacheEntry cacheEntry, out bool cacheHit)
        {
            material = null;
            cacheEntry = null;
            cacheHit = false;
            if (original == null) return false;
            string key = BuildFlatRegionMaterialKey(source, original, layers, mask);
            if (BakedMaterialCache.TryGetValue(key, out cacheEntry))
            {
                bool valid = cacheEntry != null && cacheEntry.Material != null
                    && (cacheEntry.MaterialOnly || cacheEntry.Texture != null);
                if (valid)
                {
                    material = cacheEntry.Material;
                    cacheHit = true;
                    return true;
                }
                BakedMaterialCache.Remove(key);
                if (cacheEntry != null)
                {
                    _bakedCacheBytes = Math.Max(0L, _bakedCacheBytes - cacheEntry.EstimatedBytes);
                    if (cacheEntry.Material != null) Destroy(cacheEntry.Material);
                    if (cacheEntry.Texture != null) Destroy(cacheEntry.Texture);
                    if (cacheEntry.NormalTexture != null) Destroy(cacheEntry.NormalTexture);
                }
                cacheEntry = null;
            }

            Material created = new Material(original);
            created.name = original.name + "-HeatTilePainter-FlatRegion-" + mask;
            Texture2D createdTexture = null;
            Texture2D createdNormal = null;
            long estimatedBytes = 0L;
            int changedTexels = 0;
            if (TryConfigureDirectRegionMaterial(source, created, layers, mask))
            {
                cacheEntry = new BakedCacheEntry
                {
                    Key = key,
                    Material = created,
                    Texture = null,
                    NormalTexture = null,
                    ChangedTexels = 0,
                    RasterizedTriangles = 0,
                    LastUsedFrame = Time.frameCount,
                    EstimatedBytes = 0L,
                    MaterialOnly = true
                };
                EvictUnusedBakeIfNeeded(0L);
                BakedMaterialCache[key] = cacheEntry;
                material = created;
                return true;
            }
            if (original.HasProperty("_MainTex"))
            {
                Texture sourceTexture = original.GetTexture("_MainTex");
                int width = sourceTexture == null ? UntexturedBakeDimension
                    : Mathf.Clamp(sourceTexture.width, 16, MaximumBakeDimension);
                int height = sourceTexture == null ? UntexturedBakeDimension
                    : Mathf.Clamp(sourceTexture.height, 16, MaximumBakeDimension);
                TexturePixels sourcePixels = SourcePixelsFor(sourceTexture, width, height);
                if (sourcePixels == null || sourcePixels.Pixels == null)
                {
                    Destroy(created);
                    return false;
                }
                Color32[] originalPixels = sourcePixels.Pixels;
                Color32[] composite = new Color32[originalPixels.Length];
                Array.Copy(originalPixels, composite, originalPixels.Length);
                Color materialTint = original.HasProperty("_Color")
                    ? original.GetColor("_Color") : Color.white;
                TexturePixels[] patterns = new TexturePixels[layers.Count];
                TexturePixels[] patternNormals = new TexturePixels[layers.Count];
                float[] repeats = new float[layers.Count];
                Vector2[] phases = new Vector2[layers.Count];
                bool wantsNormal = original.HasProperty("_BumpMap");
                bool hasPaintNormal = false;
                for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                {
                    if ((mask & (1 << layerIndex)) == 0) continue;
                    HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(
                        layers[layerIndex].TextureId);
                    if (definition == null || definition.IsFlatColor) continue;
                    patterns[layerIndex] = PatternPixelsFor(layers[layerIndex].TextureId);
                    if (wantsNormal)
                    {
                        patternNormals[layerIndex] = NormalPixelsFor(layers[layerIndex].TextureId);
                        if (patternNormals[layerIndex] != null) hasPaintNormal = true;
                    }
                    repeats[layerIndex] = FlatPatternRepeats(source, definition);
                    phases[layerIndex] = FlatPatternPhase(source, definition);
                }

                Color32[] originalNormalPixels = null;
                Color32[] compositeNormals = null;
                if (wantsNormal && hasPaintNormal)
                {
                    Texture sourceNormal = original.GetTexture("_BumpMap");
                    TexturePixels normalPixels = SourcePixelsFor(sourceNormal, width, height);
                    if (normalPixels != null && normalPixels.Pixels != null)
                        originalNormalPixels = normalPixels.Pixels;
                    else
                    {
                        originalNormalPixels = new Color32[width * height];
                        Color32 flatNormal = new Color32(128, 128, 255, 128);
                        for (int i = 0; i < originalNormalPixels.Length; i++)
                            originalNormalPixels[i] = flatNormal;
                    }
                    compositeNormals = new Color32[originalNormalPixels.Length];
                    Array.Copy(originalNormalPixels, compositeNormals, originalNormalPixels.Length);
                }
                ForRows(height, delegate(int y)
                {
                    int start = y * width;
                    int end = start + width;
                    for (int pixel = start; pixel < end; pixel++)
                    {
                        Color value = originalPixels[pixel];
                        Color normalValue = compositeNormals == null
                            ? Color.black : (Color)originalNormalPixels[pixel];
                        bool priorIsolated = false;
                        for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                        {
                            if ((mask & (1 << layerIndex)) == 0) continue;
                            HeatTileLayerState layer = layers[layerIndex];
                            if (layer.IsolateOpacity || priorIsolated)
                            {
                                value = originalPixels[pixel];
                                if (compositeNormals != null) normalValue = originalNormalPixels[pixel];
                            }
                            HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
                            float alpha = Mathf.Clamp01(layer.Opacity / 100f);
                            Color32 targetPixels;
                            if (definition == null || definition.IsFlatColor || patterns[layerIndex] == null)
                                targetPixels = (Color32)layer.Tint;
                            else
                            {
                                int x = pixel - start;
                                float u = (x + 0.5f) / width * repeats[layerIndex] + phases[layerIndex].x;
                                float v = (y + 0.5f) / height * repeats[layerIndex] + phases[layerIndex].y;
                                targetPixels = SampleBilinear(patterns[layerIndex], u, v);
                                alpha *= targetPixels.a / 255f;
                            }
                            Color target = CorrectForMaterialTint(targetPixels, materialTint);
                            value = Color.Lerp(value, target, alpha);
                            if (compositeNormals != null && patternNormals[layerIndex] != null)
                            {
                                int x = pixel - start;
                                float u = (x + 0.5f) / width * repeats[layerIndex] + phases[layerIndex].x;
                                float v = (y + 0.5f) / height * repeats[layerIndex] + phases[layerIndex].y;
                                Color32 targetNormal = SampleBilinear(patternNormals[layerIndex], u, v);
                                normalValue = Color.Lerp(normalValue, targetNormal, alpha * 0.85f);
                            }
                            priorIsolated = layer.IsolateOpacity;
                        }
                        Color32 encoded = value;
                        encoded.a = originalPixels[pixel].a;
                        composite[pixel] = encoded;
                        if (compositeNormals != null) compositeNormals[pixel] = (Color32)normalValue;
                    }
                });
                createdTexture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
                createdTexture.name = original.name + "-HeatTilePainter-FlatRegion-" + mask;
                createdTexture.wrapMode = sourceTexture == null
                    ? TextureWrapMode.Repeat : sourceTexture.wrapMode;
                createdTexture.filterMode = sourceTexture == null
                    ? FilterMode.Bilinear : sourceTexture.filterMode;
                createdTexture.anisoLevel = sourceTexture == null ? 1 : sourceTexture.anisoLevel;
                createdTexture.SetPixels32(composite);
                createdTexture.Apply(true, true);
                created.SetTexture("_MainTex", createdTexture);
                estimatedBytes = EstimateTextureBytes(width, height);
                if (compositeNormals != null)
                {
                    createdNormal = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
                    createdNormal.name = original.name + "-HeatTilePainter-FlatRegion-Normal-" + mask;
                    createdNormal.wrapMode = sourceTexture == null
                        ? TextureWrapMode.Repeat : sourceTexture.wrapMode;
                    createdNormal.filterMode = FilterMode.Bilinear;
                    createdNormal.anisoLevel = sourceTexture == null ? 1 : sourceTexture.anisoLevel;
                    createdNormal.SetPixels32(compositeNormals);
                    createdNormal.Apply(true, true);
                    created.SetTexture("_BumpMap", createdNormal);
                    created.EnableKeyword("_NORMALMAP");
                    estimatedBytes += EstimateTextureBytes(width, height);
                }
            }
            else if (original.HasProperty("_Color"))
            {
                for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                {
                    if ((mask & (1 << layerIndex)) == 0) continue;
                    HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(
                        layers[layerIndex].TextureId);
                    if (definition != null && !definition.IsFlatColor)
                    {
                        // A shader without an albedo texture property cannot safely represent a
                        // pattern. Treating its default white tint as the pattern would silently
                        // corrupt emissive/untextured materials.
                        Destroy(created);
                        return false;
                    }
                }
                Color baseColor = original.GetColor("_Color");
                Color value = baseColor;
                bool priorIsolated = false;
                for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                {
                    if ((mask & (1 << layerIndex)) == 0) continue;
                    HeatTileLayerState layer = layers[layerIndex];
                    if (layer.IsolateOpacity || priorIsolated) value = baseColor;
                    value = Color.Lerp(value, layer.Tint, Mathf.Clamp01(layer.Opacity / 100f));
                    priorIsolated = layer.IsolateOpacity;
                }
                created.SetColor("_Color", value);
            }
            else
            {
                Destroy(created);
                return false;
            }

            ApplyTopPaintSurfaceProperties(created, layers, mask);
            cacheEntry = new BakedCacheEntry
            {
                Key = key,
                Material = created,
                Texture = createdTexture,
                NormalTexture = createdNormal,
                ChangedTexels = changedTexels,
                RasterizedTriangles = 0,
                LastUsedFrame = Time.frameCount,
                EstimatedBytes = estimatedBytes,
                MaterialOnly = createdTexture == null
            };
            EvictUnusedBakeIfNeeded(estimatedBytes);
            BakedMaterialCache[key] = cacheEntry;
            _bakedCacheBytes += estimatedBytes;
            material = created;
            return true;
        }

        private float FlatPatternRepeats(MeshSource source, HeatTileTextureDefinition definition)
        {
            if (source == null || source.Mesh == null || definition == null) return 1f;
            try
            {
                Vector3[] vertices = source.Mesh.vertices;
                Vector2[] uvs = source.Mesh.uv;
                int[] triangles = source.Mesh.triangles;
                if (vertices == null || uvs == null || vertices.Length != uvs.Length
                    || triangles == null) return 1f;
                double area3 = 0.0;
                double areaUv = 0.0;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
                    if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Length
                        || ib >= vertices.Length || ic >= vertices.Length) continue;
                    Vector3 a = source.SourceToPart.MultiplyPoint3x4(vertices[ia]);
                    Vector3 b = source.SourceToPart.MultiplyPoint3x4(vertices[ib]);
                    Vector3 c = source.SourceToPart.MultiplyPoint3x4(vertices[ic]);
                    area3 += Vector3.Cross(b - a, c - a).magnitude * 0.5;
                    Vector2 ua = uvs[ia], ub = uvs[ib], uc = uvs[ic];
                    areaUv += Math.Abs((ub.x - ua.x) * (uc.y - ua.y)
                        - (uc.x - ua.x) * (ub.y - ua.y)) * 0.5;
                }
                if (area3 <= 0.000001 || areaUv <= 0.000001) return 1f;
                float metresPerUv = (float)Math.Sqrt(area3 / areaUv);
                return Mathf.Clamp(metresPerUv / Mathf.Max(0.10f, definition.PatternSpan), 1f, 24f);
            }
            catch (Exception) { return 1f; }
        }

        // A single fully opaque region can bind its paint assets directly. This is the common case
        // for heat shielding and avoids a million-pixel CPU composite during craft load. Partial
        // opacity and real layer overlaps still use the cached composite path below.
        private bool TryConfigureDirectRegionMaterial(MeshSource source, Material material,
            IList<HeatTileLayerState> layers, int mask)
        {
            int selected = -1;
            for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                if ((mask & (1 << layerIndex)) == 0) continue;
                if (selected >= 0) return false;
                selected = layerIndex;
            }
            if (selected < 0) return false;
            HeatTileLayerState layer = layers[selected];
            if (layer.Opacity < 99.9f) return false;
            HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
            if (definition == null) return false;

            if (definition.IsFlatColor)
            {
                bool configured = false;
                if (material.HasProperty("_MainTex"))
                {
                    material.SetTexture("_MainTex", Texture2D.whiteTexture);
                    material.SetTextureScale("_MainTex", Vector2.one);
                    material.SetTextureOffset("_MainTex", Vector2.zero);
                    configured = true;
                }
                if (material.HasProperty("_Color"))
                {
                    material.SetColor("_Color", layer.Tint);
                    configured = true;
                }
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", layer.Tint);
                ApplyPaintSurfaceProperties(material, definition);
                return configured;
            }

            if (!material.HasProperty("_MainTex") || string.IsNullOrEmpty(definition.TexturePath))
                return false;
            Texture2D pattern = GameDatabase.Instance.GetTexture(definition.TexturePath, false);
            if (pattern == null) return false;
            pattern.wrapMode = TextureWrapMode.Repeat;
            pattern.filterMode = FilterMode.Bilinear;
            float repeats = FlatPatternRepeats(source, definition);
            Vector2 phase = FlatPatternPhase(source, definition);
            Vector2 scale = Vector2.one * repeats;
            material.SetTexture("_MainTex", pattern);
            material.SetTextureScale("_MainTex", scale);
            material.SetTextureOffset("_MainTex", phase);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", Color.white);

            if (material.HasProperty("_BumpMap") && !string.IsNullOrEmpty(definition.NormalPath))
            {
                Texture2D normal = GameDatabase.Instance.GetTexture(definition.NormalPath, true);
                if (normal != null)
                {
                    normal.wrapMode = TextureWrapMode.Repeat;
                    normal.filterMode = FilterMode.Bilinear;
                    material.SetTexture("_BumpMap", normal);
                    material.SetTextureScale("_BumpMap", scale);
                    material.SetTextureOffset("_BumpMap", phase);
                    material.EnableKeyword("_NORMALMAP");
                }
            }
            if (material.HasProperty("_SpecColor"))
            {
                float specularity = Mathf.Clamp01(definition.Specularity);
                material.SetColor("_SpecColor", new Color(specularity, specularity,
                    specularity, 1f));
            }
            if (material.HasProperty("_Shininess"))
                material.SetFloat("_Shininess", Mathf.Clamp01(definition.Shininess));
            if (material.HasProperty("_Glossiness"))
                material.SetFloat("_Glossiness", Mathf.Clamp01(definition.Shininess));
            return true;
        }

        private static void ApplyPaintSurfaceProperties(Material material,
            HeatTileTextureDefinition definition)
        {
            if (material == null || definition == null) return;
            float specularity = Mathf.Clamp01(definition.Specularity);
            float shininess = Mathf.Clamp01(definition.Shininess);
            if (definition.IsFlatColor)
            {
                // A cloned stock/mod material may carry a metallic or specular map. Leaving that
                // inherited map bound made a nominally matte tint as glossy as its source part.
                // Use a black texture rather than null so shaders with a map keyword cannot fall
                // back to a bright built-in default.
                string[] mapProperties =
                {
                    "_SpecMap", "_SpecGlossMap", "_MetallicGlossMap", "_MetalMap",
                    "_MetallicMap", "_GlossMap"
                };
                for (int i = 0; i < mapProperties.Length; i++)
                    if (material.HasProperty(mapProperties[i]))
                        material.SetTexture(mapProperties[i], Texture2D.blackTexture);
                material.DisableKeyword("_SPECGLOSSMAP");
                material.DisableKeyword("_METALLICGLOSSMAP");
                material.DisableKeyword("_METALLIC_MAP");
            }
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_SpecColor"))
                material.SetColor("_SpecColor", new Color(specularity, specularity,
                    specularity, 1f));
            if (material.HasProperty("_ReflectColor"))
                material.SetColor("_ReflectColor", new Color(specularity, specularity,
                    specularity, 1f));
            if (material.HasProperty("_Shininess")) material.SetFloat("_Shininess", shininess);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", shininess);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", shininess);
            if (material.HasProperty("_GlossMapScale")) material.SetFloat("_GlossMapScale", shininess);
        }

        private static void ApplyTopPaintSurfaceProperties(Material material,
            IList<HeatTileLayerState> layers, int mask)
        {
            HeatTileTextureDefinition top = null;
            for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                if ((mask & (1 << layerIndex)) != 0)
                    top = HeatTileTextureLibrary.Find(layers[layerIndex].TextureId);
            ApplyPaintSurfaceProperties(material, top);
        }

        private Vector2 FlatPatternPhase(MeshSource source, HeatTileTextureDefinition definition)
        {
            if (part == null || part.transform == null || source == null || definition == null)
                return Vector2.zero;
            Matrix4x4 partToCraft = RelativeMatrix(part.transform, GetCraftMappingTransform());
            Vector3 origin = partToCraft.MultiplyPoint3x4(
                source.SourceToPart.MultiplyPoint3x4(Vector3.zero));
            float span = Mathf.Max(0.10f, definition.PatternSpan);
            return new Vector2((origin.x + origin.z * 0.37f) / span,
                (origin.y + origin.z * 0.61f) / span);
        }

        private string BuildFlatRegionMaterialKey(MeshSource source, Material original,
            IList<HeatTileLayerState> layers, int mask)
        {
            ulong hash = 1469598103934665603UL;
            HashString(ref hash, "reforged-region-v2");
            // The cloned material retains every shader property, not only the albedo fields below.
            // Binding a cached clone made from another material instance could therefore import a
            // different normal/specular/emissive setup. Instance identity makes that impossible.
            HashValue(ref hash, original.GetInstanceID());
            HashString(ref hash, original.shader == null ? "" : original.shader.name);
            HashString(ref hash, original.name ?? "");
            HashValue(ref hash, original.renderQueue);
            if (source != null && source.Mesh != null)
            {
                HashValue(ref hash, source.Mesh.GetInstanceID());
                HashValue(ref hash, source.Mesh.vertexCount);
                HashValue(ref hash, source.Mesh.subMeshCount);
            }
            if (original.HasProperty("_MainTex"))
            {
                Texture texture = original.GetTexture("_MainTex");
                HashValue(ref hash, texture == null ? 0 : texture.GetInstanceID());
                HashValue(ref hash, texture == null ? 1 : texture.width);
                HashValue(ref hash, texture == null ? 1 : texture.height);
                HashVector2(ref hash, original.GetTextureScale("_MainTex"));
                HashVector2(ref hash, original.GetTextureOffset("_MainTex"));
            }
            if (original.HasProperty("_Color")) HashColor(ref hash, original.GetColor("_Color"));
            HashValue(ref hash, mask);
            for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                if ((mask & (1 << layerIndex)) == 0) continue;
                HeatTileLayerState layer = layers[layerIndex];
                HashString(ref hash, layer.TextureId);
                HashColor(ref hash, layer.Tint);
                HashFloat(ref hash, layer.Opacity);
                HashValue(ref hash, layer.IsolateOpacity ? 1 : 0);
                HeatTileTextureDefinition definition = HeatTileTextureLibrary.Find(layer.TextureId);
                if (definition != null && !definition.IsFlatColor)
                {
                    HashFloat(ref hash, FlatPatternRepeats(source, definition));
                    HashVector2(ref hash, FlatPatternPhase(source, definition));
                }
            }
            return "R" + hash.ToString("X16", CultureInfo.InvariantCulture);
        }

        private void RestoreFlatRegionPaint()
        {
            for (int i = 0; i < _flatRegionBindings.Count; i++)
            {
                FlatRegionBinding binding = _flatRegionBindings[i];
                if (binding == null) continue;
                RestoreFlatRegionBindingState(binding);
                for (int entryIndex = 0; entryIndex < binding.CacheEntries.Count; entryIndex++)
                {
                    BakedCacheEntry entry = binding.CacheEntries[entryIndex];
                    if (entry != null) entry.RefCount = Mathf.Max(0, entry.RefCount - 1);
                }
                if (binding.AppliedMesh != null) Destroy(binding.AppliedMesh);
            }
            _flatRegionBindings.Clear();
        }

        private static void RestoreFlatRegionBindingState(FlatRegionBinding binding)
        {
            if (binding == null) return;
            bool ownsMesh = binding.Filter != null
                && binding.Filter.sharedMesh == binding.AppliedMesh;
            bool ownsMaterials = binding.Renderer != null
                && SameMaterialArray(binding.Renderer.sharedMaterials, binding.AppliedMaterials);

            // Restore the material array while the generated mesh (and its submesh count) is still
            // installed. Unity can resize/truncate Renderer.sharedMaterials after a mesh swap;
            // checking materials after restoring the mesh caused our ownership test to fail and
            // left paint-region materials mapped onto the original model.
            if (binding.Renderer != null && (ownsMesh || ownsMaterials))
                binding.Renderer.sharedMaterials = binding.OriginalMaterials;
            if (ownsMesh) binding.Filter.sharedMesh = binding.OriginalMesh;
        }

        private bool TryResolveFlatRegionOriginal(MeshFilter filter, MeshRenderer renderer,
            out Mesh mesh, out Material[] materials)
        {
            mesh = filter == null ? null : filter.sharedMesh;
            materials = renderer == null ? null : renderer.sharedMaterials;
            for (int i = 0; i < _flatRegionBindings.Count; i++)
            {
                FlatRegionBinding binding = _flatRegionBindings[i];
                if (binding == null || binding.Filter != filter || binding.Renderer != renderer)
                    continue;
                // If a procedural module has already replaced either object, its new live source
                // wins and the stale binding will be discarded by the scheduled rebuild.
                if (filter.sharedMesh != binding.AppliedMesh
                    || !SameMaterialArray(renderer.sharedMaterials, binding.AppliedMaterials))
                    return false;
                mesh = binding.OriginalMesh;
                materials = binding.OriginalMaterials;
                return mesh != null && materials != null;
            }
            return false;
        }
    }
}
