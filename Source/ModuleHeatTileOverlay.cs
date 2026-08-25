using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace HeatTileOverlayPrototype
{
    public sealed class HeatTileLayerState
    {
        public bool Visible = true;
        public string TextureId = "legacy_hrsi";
        public int Direction = 3;
        public float Coverage = 35f;
        public float Opacity = 100f;
        public float EdgeBlend = 3f;
        public int EdgeStyle = 1;
        public bool DirectionFacingOnly = true;
        public float SurfaceWrap = 82f;
        public Color Tint = new Color(0.12f, 0.13f, 0.14f, 1f);
        public Vector3 CustomDirection = Vector3.zero;
        public int MirrorMode;
        public int MirrorAxis;
        public bool IsolateOpacity;
        public float PlaneOffset;

        public HeatTileLayerState Clone()
        {
            return new HeatTileLayerState
            {
                Visible = Visible,
                TextureId = TextureId,
                Direction = Direction,
                Coverage = Coverage,
                Opacity = Opacity,
                EdgeBlend = EdgeBlend,
                EdgeStyle = EdgeStyle,
                DirectionFacingOnly = DirectionFacingOnly,
                SurfaceWrap = SurfaceWrap,
                Tint = Tint,
                CustomDirection = CustomDirection,
                MirrorMode = MirrorMode,
                MirrorAxis = MirrorAxis,
                IsolateOpacity = IsolateOpacity,
                PlaneOffset = PlaneOffset
            };
        }
    }

    [KSPModule("Heat Tile Overlay State")]
    public sealed partial class ModuleHeatTileOverlay : PartModule, IPartMassModifier
    {
        private const string GeneratedObjectPrefix = "__HeatTilePainter";
        // Four smoothstep bands are visually continuous across the intentionally narrow fade, while
        // avoiding the old 17 draw passes per source renderer. This matters enormously on animated
        // parts such as landing gear, which can expose ten independent source renderers apiece.
        private const int SoftBandCount = 4;

        // Retained so 0.1-0.5 craft state migrates into the first real layer.
        [KSPField(isPersistant = true)] public bool tilesActive = false;
        [KSPField(isPersistant = true)] public float coveragePercent = 35f;
        [KSPField(isPersistant = true)] public int coverageDirection = 3;
        [KSPField(isPersistant = true)] public string textureId = "legacy_hrsi";
        [KSPField(isPersistant = true)] public float opacityPercent = 100f;
        [KSPField(isPersistant = true)] public float edgeBlendPercent = 3f;
        [KSPField(isPersistant = true)] public int edgeStyle = 1;
        [KSPField(isPersistant = true)] public bool directionFacingOnly = true;
        [KSPField(isPersistant = true)] public float surfaceWrapDegrees = 82f;
        [KSPField(isPersistant = true)] public float tintRed = 0.12f;
        [KSPField(isPersistant = true)] public float tintGreen = 0.13f;
        [KSPField(isPersistant = true)] public float tintBlue = 0.14f;
        [KSPField(isPersistant = true)] public string layerData = "";
        [KSPField(isPersistant = true)] public float mapOffsetX = 0f;
        [KSPField(isPersistant = true)] public float mapOffsetY = 0f;
        [KSPField(isPersistant = true)] public float mapOffsetZ = 0f;
        // Captured in the editor after the selected variant has settled. Flight uses exactly these
        // source renderers, eligible submeshes, and source-to-part transforms, so B9/variant modules
        // cannot silently add a second paint target or change the coverage plane after launch.
        [KSPField(isPersistant = true)] public string sourceCalibrationData = "";
        [KSPField(isPersistant = true)] public bool thermalProtectionEnabled = false;
        [KSPField] public float surfaceOffset = 0f;

        private const float ThermalMassFraction = 0.05f;
        private const double InternalTemperatureMultiplier = 1.35;
        private const double SkinTemperatureMultiplier = 1.50;
        private const double InternalTemperatureCap = 3000.0;
        private const double SkinTemperatureCap = 3300.0;

        private readonly List<HeatTileLayerState> _layers = new List<HeatTileLayerState>();
        private readonly Dictionary<string, SourceCalibration> _sourceCalibrations =
            new Dictionary<string, SourceCalibration>(StringComparer.Ordinal);
        private bool _layersInitialized;
        private bool _sourceCalibrationsInitialized;
        private bool _reportedCalibrationMismatch;
        private int _rebuildAtFrame = -1;
        private int _revision;
        private bool _initialRebuildQueued;
        private bool _scheduledRebuildQueued;
        private int _initialRebuildReadyFrame;
        private static readonly Queue<ModuleHeatTileOverlay> InitialRebuildQueue =
            new Queue<ModuleHeatTileOverlay>();
        private static readonly HashSet<int> InitialRebuildIds = new HashSet<int>();
        private static readonly Queue<ModuleHeatTileOverlay> ScheduledRebuildQueue =
            new Queue<ModuleHeatTileOverlay>();
        private static readonly HashSet<int> ScheduledRebuildIds = new HashSet<int>();

        private sealed class MeshSource
        {
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public string Key;
            public Matrix4x4 SourceToPart;
            public bool[] EligibleSubmeshes;
        }

        private sealed class SourceCalibration
        {
            public string Key;
            public Matrix4x4 SourceToPart;
            public bool[] EligibleSubmeshes;
        }

        private sealed class MeshBuildData
        {
            public readonly List<Vector3> Vertices = new List<Vector3>(8192);
            public readonly List<Vector3> Normals = new List<Vector3>(8192);
            public readonly List<Vector2> Uvs = new List<Vector2>(8192);
            public List<int>[] TriangleSets;
            public int EligibleSubmeshes;
            public int ExcludedSubmeshes;
            public int FacingRejectedTriangles;

            public int IndexCount
            {
                get
                {
                    int count = 0;
                    if (TriangleSets != null)
                        for (int i = 0; i < TriangleSets.Length; i++) count += TriangleSets[i].Count;
                    return count;
                }
            }
        }

        private sealed class CoverageContext
        {
            public HeatTileLayerState State;
            public Vector3 DirectionA;
            public Vector3 DirectionB;
            public float ClipA;
            public float ClipB;
            public float BlendA;
            public float BlendB;
            public int BlendBands;

            public bool Mirrored { get { return State != null && State.MirrorMode != 0; } }
        }

        private struct ClipVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public float Height;

            public ClipVertex(Vector3 position, Vector3 normal, float height)
            {
                Position = position;
                Normal = normal;
                Height = height;
            }
        }

        public int Revision { get { return _revision; } }
        public int LayerCount { get { EnsureLayers(); return _layers.Count; } }
        public bool ThermalProtectionEnabled
        {
            get { EnsureLayers(); return thermalProtectionEnabled && _layers.Count > 0; }
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            // KSP creates Instantly add-ons before every GameEvents channel exists. The 0.22
            // scheduler subscribed at that point, threw from Awake, and therefore never pumped
            // saved-layer restores. A live PartModule is a reliable, scene-ready bootstrap; the
            // guard below still creates exactly one scheduler for the entire game.
            HeatTileOverlayScheduler.EnsureRunning();
            HeatTileTextureLibrary.EnsureLoaded();
            EnsureLayers();
            EnsureSourceCalibrations();
            ApplyThermalProtection();
            if (_layers.Count > 0) EnqueueInitialRebuild();
        }

        public void OnDestroy()
        {
            CancelInitialRebuild();
            CancelScheduledRebuild();
            RestorePrefabThermalLimits();
            DestroyOverlay();
        }

        private void EnqueueInitialRebuild()
        {
            if (_initialRebuildQueued || _layers.Count == 0) return;
            int id = GetInstanceID();
            if (!InitialRebuildIds.Add(id)) return;
            _initialRebuildQueued = true;
            // Let variants and procedural modules finish their first scene setup before discovery.
            _initialRebuildReadyFrame = Time.frameCount + 2;
            CancelScheduledRebuild();
            InitialRebuildQueue.Enqueue(this);
        }

        private void CancelInitialRebuild()
        {
            if (!_initialRebuildQueued) return;
            _initialRebuildQueued = false;
            InitialRebuildIds.Remove(GetInstanceID());
        }

        private static bool PumpInitialRebuildQueue()
        {
            while (InitialRebuildQueue.Count > 0)
            {
                ModuleHeatTileOverlay candidate = InitialRebuildQueue.Peek();
                if (candidate == null)
                {
                    InitialRebuildQueue.Dequeue();
                    continue;
                }
                if (!candidate._initialRebuildQueued)
                {
                    InitialRebuildQueue.Dequeue();
                    InitialRebuildIds.Remove(candidate.GetInstanceID());
                    continue;
                }
                if (Time.frameCount < candidate._initialRebuildReadyFrame) return false;

                InitialRebuildQueue.Dequeue();
                candidate._initialRebuildQueued = false;
                InitialRebuildIds.Remove(candidate.GetInstanceID());
                candidate.EnsureLayers();
                if (candidate._layers.Count > 0 && candidate.part != null)
                    try
                    {
                        candidate.RebuildOverlay();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError("[HeatTilePainter] Queued paint restore failed on "
                            + (candidate.part.partInfo == null ? candidate.part.name
                                : candidate.part.partInfo.name)
                            + ": " + exception.Message);
                    }
                // Exactly one valid paint restore is allowed per frame.
                return true;
            }
            return false;
        }

        private void EnqueueScheduledRebuild()
        {
            if (_scheduledRebuildQueued) return;
            int id = GetInstanceID();
            if (!ScheduledRebuildIds.Add(id)) return;
            _scheduledRebuildQueued = true;
            ScheduledRebuildQueue.Enqueue(this);
        }

        private void CancelScheduledRebuild()
        {
            if (!_scheduledRebuildQueued)
            {
                _rebuildAtFrame = -1;
                return;
            }
            _scheduledRebuildQueued = false;
            _rebuildAtFrame = -1;
            ScheduledRebuildIds.Remove(GetInstanceID());
        }

        private static bool PumpScheduledRebuildQueue()
        {
            int candidatesThisFrame = ScheduledRebuildQueue.Count;
            for (int i = 0; i < candidatesThisFrame; i++)
            {
                ModuleHeatTileOverlay candidate = ScheduledRebuildQueue.Dequeue();
                if (candidate == null) continue;
                int id = candidate.GetInstanceID();
                if (!candidate._scheduledRebuildQueued)
                {
                    ScheduledRebuildIds.Remove(id);
                    continue;
                }
                if (candidate._initialRebuildQueued)
                {
                    candidate._scheduledRebuildQueued = false;
                    candidate._rebuildAtFrame = -1;
                    ScheduledRebuildIds.Remove(id);
                    continue;
                }
                if (candidate._rebuildAtFrame < 0)
                {
                    candidate._scheduledRebuildQueued = false;
                    ScheduledRebuildIds.Remove(id);
                    continue;
                }
                if (Time.frameCount < candidate._rebuildAtFrame)
                {
                    ScheduledRebuildQueue.Enqueue(candidate);
                    continue;
                }

                candidate._scheduledRebuildQueued = false;
                candidate._rebuildAtFrame = -1;
                ScheduledRebuildIds.Remove(id);
                candidate.EnsureLayers();
                if (candidate._layers.Count > 0 && candidate.part != null)
                    try { candidate.RebuildOverlay(); }
                    catch (Exception exception)
                    {
                        Debug.LogError("[HeatTilePainter] Queued repaint failed on "
                            + (candidate.part.partInfo == null ? candidate.part.name
                                : candidate.part.partInfo.name)
                            + ": " + exception.Message);
                    }
                return true;
            }
            return false;
        }

        internal static void PumpRebuildQueues()
        {
            // One global scheduler replaces a Unity LateUpdate on every patched part. Limit the
            // combined restore/repaint workload to one part per frame to avoid editor/load spikes.
            if (!PumpInitialRebuildQueue()) PumpScheduledRebuildQueue();
        }

        internal static void ClearRebuildQueues()
        {
            foreach (ModuleHeatTileOverlay candidate in InitialRebuildQueue)
                if (candidate != null) candidate._initialRebuildQueued = false;
            foreach (ModuleHeatTileOverlay candidate in ScheduledRebuildQueue)
                if (candidate != null)
                {
                    candidate._scheduledRebuildQueued = false;
                    candidate._rebuildAtFrame = -1;
                }
            InitialRebuildQueue.Clear();
            ScheduledRebuildQueue.Clear();
            InitialRebuildIds.Clear();
            ScheduledRebuildIds.Clear();
        }

        public HeatTileLayerState GetLayer(int index)
        {
            EnsureLayers();
            return index < 0 || index >= _layers.Count ? null : _layers[index].Clone();
        }

        public HeatTileLayerState GetTopLayer()
        {
            EnsureLayers();
            return _layers.Count == 0 ? null : _layers[_layers.Count - 1].Clone();
        }

        public int AddLayer(HeatTileLayerState state)
        {
            EnsureLayers();
            _layers.Add(Sanitize(state));
            CommitLayers();
            return _layers.Count - 1;
        }

        public bool UpdateLayer(int index, HeatTileLayerState state)
        {
            EnsureLayers();
            if (index < 0 || index >= _layers.Count) return false;
            bool visible = _layers[index].Visible;
            _layers[index] = Sanitize(state);
            _layers[index].Visible = visible;
            CommitLayers();
            return true;
        }

        public bool SetLayerVisible(int index, bool visible)
        {
            EnsureLayers();
            if (index < 0 || index >= _layers.Count || _layers[index].Visible == visible) return false;
            _layers[index].Visible = visible;
            CommitLayers();
            return true;
        }

        public bool MoveLayer(int index, int delta)
        {
            EnsureLayers();
            int destination = index + delta;
            if (index < 0 || index >= _layers.Count || destination < 0 || destination >= _layers.Count) return false;
            HeatTileLayerState moving = _layers[index];
            _layers.RemoveAt(index);
            _layers.Insert(destination, moving);
            CommitLayers();
            return true;
        }

        public bool RemoveLayer(int index)
        {
            EnsureLayers();
            if (index < 0 || index >= _layers.Count) return false;
            _layers.RemoveAt(index);
            if (_layers.Count == 0) thermalProtectionEnabled = false;
            CommitLayers();
            return true;
        }

        public bool RemoveTopLayer()
        {
            EnsureLayers();
            return RemoveLayer(_layers.Count - 1);
        }

        public bool RemoveAllLayers()
        {
            EnsureLayers();
            if (_layers.Count == 0 && !thermalProtectionEnabled) return false;
            _layers.Clear();
            thermalProtectionEnabled = false;
            CommitLayers();
            return true;
        }

        public bool SetThermalProtection(bool enabled)
        {
            EnsureLayers();
            enabled = enabled && _layers.Count > 0;
            if (thermalProtectionEnabled == enabled) return false;
            thermalProtectionEnabled = enabled;
            ApplyThermalProtection();
            return true;
        }

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit)
        {
            return ThermalProtectionEnabled ? defaultMass * ThermalMassFraction : 0f;
        }

        public ModifierChangeWhen GetModuleMassChangeWhen()
        {
            return ModifierChangeWhen.CONSTANTLY;
        }

        // Backward-compatible single-layer API.
        public void ApplySettings(bool active, string newTextureId, int newDirection, float newCoverage,
            float newOpacity, float newEdgeBlend, bool newDirectionFacingOnly, float newSurfaceWrap, Color newTint)
        {
            EnsureLayers();
            _layers.Clear();
            if (active)
            {
                _layers.Add(Sanitize(new HeatTileLayerState
                {
                    TextureId = newTextureId,
                    Direction = newDirection,
                    Coverage = newCoverage,
                    Opacity = newOpacity,
                    EdgeBlend = newEdgeBlend,
                    EdgeStyle = newEdgeBlend > 0.001f ? 1 : 0,
                    DirectionFacingOnly = newDirectionFacingOnly,
                    SurfaceWrap = newSurfaceWrap,
                    Tint = newTint
                }));
            }
            CommitLayers();
        }

        public Mesh CreatePreviewMesh(HeatTileLayerState requestedState, Color previewColor)
        {
            if (part == null || part.transform == null || requestedState == null) return null;
            MeshBuildData data = BuildGeometry(Sanitize(requestedState), surfaceOffset + 0.004f);
            if (data == null || data.IndexCount == 0) return null;
            List<int> allTriangles = new List<int>(data.IndexCount);
            for (int i = 0; i < data.TriangleSets.Length; i++) allTriangles.AddRange(data.TriangleSets[i]);
            Mesh mesh = new Mesh();
            mesh.name = part.partInfo.name + "-heat-tile-preview";
            if (data.Vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.Uvs);
            List<Color> colors = new List<Color>(data.Vertices.Count);
            for (int i = 0; i < data.Vertices.Count; i++) colors.Add(previewColor);
            mesh.SetColors(colors);
            mesh.SetTriangles(allTriangles, 0, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        public bool TryGetPreviewBounds(HeatTileLayerState requestedState, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (part == null || part.transform == null || requestedState == null) return false;
            MeshBuildData data = BuildGeometry(Sanitize(requestedState), surfaceOffset + 0.004f);
            if (data == null || data.Vertices.Count == 0 || data.IndexCount == 0) return false;
            bounds = new Bounds(data.Vertices[0], Vector3.zero);
            for (int i = 1; i < data.Vertices.Count; i++) bounds.Encapsulate(data.Vertices[i]);
            return true;
        }

        public bool TryGetCoveragePlane(HeatTileLayerState requestedState, out Vector3 localPoint,
            out float projectedDepth)
        {
            localPoint = Vector3.zero;
            projectedDepth = 0f;
            if (part == null || part.transform == null || requestedState == null) return false;
            HeatTileLayerState state = Sanitize(requestedState);
            List<MeshSource> sources = FindMeshSources();
            if (sources.Count == 0) return false;
            Vector3 direction = CoverageDirection(state);
            float minimum = float.PositiveInfinity;
            float maximum = float.NegativeInfinity;
            FindHeightRange(sources, direction, ref minimum, ref maximum);
            projectedDepth = maximum - minimum;
            if (float.IsInfinity(minimum) || float.IsInfinity(maximum) || projectedDepth < 0.0001f)
                return false;
            float clip = Mathf.Lerp(minimum, maximum, EffectiveCutPercent(state) / 100f);
            // Any point whose projection equals clip lies on the infinite coverage plane. Using the
            // part-local axis line keeps the stock gizmo stable as procedural geometry settles.
            localPoint = direction * clip;
            return true;
        }

        public static float EffectiveCutPercent(HeatTileLayerState state)
        {
            if (state == null) return 65f;
            return Mathf.Clamp(100f - state.Coverage + state.PlaneOffset, 0f, 100f);
        }

        public static Vector3 DirectionVector(int direction)
        {
            switch (direction)
            {
                case 0: return Vector3.right;
                case 1: return Vector3.left;
                case 2: return Vector3.up;
                case 3: return Vector3.down;
                case 4: return Vector3.forward;
                case 5: return Vector3.back;
                default: return Vector3.down;
            }
        }

        public static Vector3 CoverageDirection(HeatTileLayerState state)
        {
            if (state != null && state.CustomDirection.sqrMagnitude > 0.0001f)
                return state.CustomDirection.normalized;
            return DirectionVector(state == null ? 3 : state.Direction);
        }

        public static Vector3 MirroredDirection(HeatTileLayerState state)
        {
            Vector3 direction = CoverageDirection(state);
            int axis = state == null ? 0 : Mathf.Clamp(state.MirrorAxis, 0, 2);
            if (axis == 0) direction.x = -direction.x;
            else if (axis == 1) direction.y = -direction.y;
            else direction.z = -direction.z;
            return direction.normalized;
        }

        private void EnsureLayers()
        {
            if (_layersInitialized) return;
            _layersInitialized = true;
            _layers.Clear();
            if (!string.IsNullOrEmpty(layerData)) DeserializeLayers(layerData);
            if (_layers.Count == 0 && tilesActive)
            {
                _layers.Add(Sanitize(new HeatTileLayerState
                {
                    TextureId = textureId,
                    Direction = coverageDirection,
                    Coverage = coveragePercent,
                    Opacity = opacityPercent,
                    EdgeBlend = edgeBlendPercent,
                    EdgeStyle = edgeBlendPercent > 0.001f ? 1 : 0,
                    DirectionFacingOnly = directionFacingOnly,
                    SurfaceWrap = surfaceWrapDegrees,
                    Tint = new Color(tintRed, tintGreen, tintBlue, 1f)
                }));
                SaveLayers();
                Debug.Log("[HeatTilePainter] Migrated legacy state to a layer on " + part.partInfo.name);
            }
            SyncLegacyFields();
        }

        private static HeatTileLayerState Sanitize(HeatTileLayerState source)
        {
            HeatTileLayerState state = source == null ? new HeatTileLayerState() : source.Clone();
            state.TextureId = HeatTileTextureLibrary.Find(state.TextureId).Id;
            state.Direction = Mathf.Clamp(state.Direction, 0, 5);
            state.CustomDirection = state.CustomDirection.sqrMagnitude > 0.0001f
                ? state.CustomDirection.normalized : DirectionVector(state.Direction);
            // Mirror intersection was never supported by the active Reforged bridge. Normalize
            // legacy mode 2 to the implemented mirrored-union behavior when state is materialized.
            state.MirrorMode = state.MirrorMode == 0 ? 0 : 1;
            state.MirrorAxis = Mathf.Clamp(state.MirrorAxis, 0, 2);
            state.Coverage = Mathf.Clamp(state.Coverage, 0f, 100f);
            state.PlaneOffset = Mathf.Clamp(state.PlaneOffset, -100f, 100f);
            state.Opacity = Mathf.Clamp(state.Opacity, 0f, 100f);
            state.EdgeStyle = Mathf.Clamp(state.EdgeStyle, 0, 3);
            float maximumEdge = state.EdgeStyle == 1 ? 8f : (state.EdgeStyle == 2 ? 4f : (state.EdgeStyle == 3 ? 5f : 0f));
            state.EdgeBlend = state.EdgeStyle == 0 ? 0f : Mathf.Clamp(state.EdgeBlend, 0.25f, maximumEdge);
            state.SurfaceWrap = Mathf.Clamp(state.SurfaceWrap, 30f, 110f);
            state.Tint = new Color(Mathf.Clamp01(state.Tint.r), Mathf.Clamp01(state.Tint.g), Mathf.Clamp01(state.Tint.b), 1f);
            return state;
        }

        private void CommitLayers()
        {
            if (_layers.Count == 0) thermalProtectionEnabled = false;
            SaveLayers();
            SyncLegacyFields();
            ApplyThermalProtection();
            _revision++;
            if (_layers.Count > 0) RequestRebuild(1);
            else
            {
                CancelInitialRebuild();
                CancelScheduledRebuild();
                DestroyOverlay();
            }
        }

        private void SaveLayers()
        {
            List<string> values = new List<string>(_layers.Count);
            for (int i = 0; i < _layers.Count; i++) values.Add(SerializeLayer(_layers[i]));
            layerData = string.Join(";", values.ToArray());
        }

        private static string SerializeLayer(HeatTileLayerState state)
        {
            return (state.Visible ? "1" : "0") + "|" + state.TextureId + "|"
                + state.Direction.ToString(CultureInfo.InvariantCulture) + "|"
                + state.Coverage.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.Opacity.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.EdgeBlend.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.EdgeStyle.ToString(CultureInfo.InvariantCulture) + "|"
                + (state.DirectionFacingOnly ? "1" : "0") + "|"
                + state.SurfaceWrap.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.Tint.r.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.Tint.g.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.Tint.b.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.CustomDirection.x.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.CustomDirection.y.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.CustomDirection.z.ToString("R", CultureInfo.InvariantCulture) + "|"
                + state.MirrorMode.ToString(CultureInfo.InvariantCulture) + "|"
                + state.MirrorAxis.ToString(CultureInfo.InvariantCulture) + "|"
                + (state.IsolateOpacity ? "1" : "0") + "|"
                + state.PlaneOffset.ToString("R", CultureInfo.InvariantCulture);
        }

        private void DeserializeLayers(string data)
        {
            string[] serializedLayers = data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < serializedLayers.Length; i++)
            {
                string[] values = serializedLayers[i].Split('|');
                if (values.Length < 12) continue;
                _layers.Add(Sanitize(new HeatTileLayerState
                {
                    Visible = values[0] != "0",
                    TextureId = values[1],
                    Direction = ParseInt(values[2], 3),
                    Coverage = ParseFloat(values[3], 35f),
                    Opacity = ParseFloat(values[4], 100f),
                    EdgeBlend = ParseFloat(values[5], 3f),
                    EdgeStyle = ParseInt(values[6], 1),
                    DirectionFacingOnly = values[7] != "0",
                    SurfaceWrap = ParseFloat(values[8], 82f),
                    Tint = new Color(ParseFloat(values[9], 0.12f), ParseFloat(values[10], 0.13f), ParseFloat(values[11], 0.14f), 1f),
                    CustomDirection = values.Length >= 15
                        ? new Vector3(ParseFloat(values[12], 0f), ParseFloat(values[13], 0f), ParseFloat(values[14], 0f))
                        : Vector3.zero,
                    MirrorMode = values.Length >= 16 ? ParseInt(values[15], 0) : 0,
                    MirrorAxis = values.Length >= 17 ? ParseInt(values[16], 0) : 0,
                    IsolateOpacity = values.Length >= 18 && values[17] != "0",
                    PlaneOffset = values.Length >= 19 ? ParseFloat(values[18], 0f) : 0f
                }));
            }
        }

        private void ApplyThermalProtection()
        {
            if (part == null || part.partInfo == null || part.partInfo.partPrefab == null) return;
            Part prefab = part.partInfo.partPrefab;
            if (!thermalProtectionEnabled || _layers.Count == 0)
            {
                part.maxTemp = prefab.maxTemp;
                part.skinMaxTemp = prefab.skinMaxTemp;
                return;
            }
            part.maxTemp = Math.Min(prefab.maxTemp * InternalTemperatureMultiplier,
                InternalTemperatureCap);
            part.skinMaxTemp = Math.Min(prefab.skinMaxTemp * SkinTemperatureMultiplier,
                SkinTemperatureCap);
        }

        private void RestorePrefabThermalLimits()
        {
            if (part == null || part.partInfo == null || part.partInfo.partPrefab == null) return;
            part.maxTemp = part.partInfo.partPrefab.maxTemp;
            part.skinMaxTemp = part.partInfo.partPrefab.skinMaxTemp;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static float ParseFloat(string value, float fallback)
        {
            float parsed;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private void SyncLegacyFields()
        {
            tilesActive = _layers.Count > 0;
            if (_layers.Count == 0) return;
            HeatTileLayerState top = _layers[_layers.Count - 1];
            textureId = top.TextureId;
            coverageDirection = top.Direction;
            coveragePercent = top.Coverage;
            opacityPercent = top.Opacity;
            edgeBlendPercent = top.EdgeBlend;
            edgeStyle = top.EdgeStyle;
            directionFacingOnly = top.DirectionFacingOnly;
            surfaceWrapDegrees = top.SurfaceWrap;
            tintRed = top.Tint.r;
            tintGreen = top.Tint.g;
            tintBlue = top.Tint.b;
        }

        internal void OnEditorShipModified(ShipConstruct ship)
        {
            EnsureLayers();
            // CommitLayers already schedules paint edits. The only unrelated editor change that
            // needs a rebuild is a settled geometry edit on the currently selected procedural part.
            if (_layers.Count > 0 && IsProceduralMeshPart() && HighLogic.LoadedSceneIsEditor
                && EditorLogic.fetch != null && EditorLogic.SelectedPart == part) RequestRebuild(3);
        }

        private bool IsB9ProceduralPart()
        {
            if (part == null || part.partInfo == null) return false;
            string name = part.partInfo == null ? "" : part.partInfo.name;
            if (!string.IsNullOrEmpty(name)
                && name.StartsWith("B9.Aero.Wing.Procedural", StringComparison.OrdinalIgnoreCase)) return true;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module != null && module.GetType().Name.IndexOf("WingProcedural",
                    StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private bool IsProceduralMeshPart()
        {
            if (part == null) return false;
            if (IsB9ProceduralPart()) return true;
            string partName = part.partInfo == null ? part.name : part.partInfo.name;
            if (!string.IsNullOrEmpty(partName)
                && partName.IndexOf("procedural", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null) continue;
                string typeName = module.GetType().Name;
                string moduleName = module.moduleName ?? "";
                if (typeName.IndexOf("Procedural", StringComparison.OrdinalIgnoreCase) >= 0
                    || moduleName.IndexOf("Procedural", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        internal void OnVariantApplied(Part changedPart, PartVariant variant)
        {
            if (SuppressVariantRefresh) return;
            EnsureLayers();
            if (_layers.Count > 0 && changedPart == part)
            {
                // A stock variant legitimately replaces source materials. Discard the prior
                // pristine snapshot, let the selected variant settle, then derive paint anew.
                ClearReforgedPaint(true);
                RequestRebuild(2);
            }
        }

        private void RequestRebuild(int frames)
        {
            if (_layers.Count == 0) { CancelScheduledRebuild(); DestroyOverlay(); return; }
            // An initial queued build will consume the latest layer/source state; do not duplicate it.
            if (_initialRebuildQueued) return;
            int target = Time.frameCount + Math.Max(1, frames);
            if (_rebuildAtFrame < target) _rebuildAtFrame = target;
            EnqueueScheduledRebuild();
        }

        private void RebuildOverlay()
        {
            EnsureLayers();
            if (_layers.Count == 0 || part == null || part.transform == null) return;
            // The bespoke UV-bake/flat-region backend is retired. Reforged's pristine/prefab
            // ownership is the only code allowed to alter materials or create a disposable split.
            if (TryRebuildReforgedPaint())
            {
                return;
            }
            Debug.LogWarning("[HeatTilePainter] Reforged core safely declined "
                + part.partInfo.name + "; no compatibility overlays were created.");
        }

        private void BuildLayerOverlay(HeatTileLayerState state, int layerIndex)
        {
#if false // Retired in 0.19: persistent overlay renderers are intentionally not compiled.
            // Layers are ordered by render queue and never write depth. Keep their source-depth
            // geometry exact; the renderer follower applies a constant-cost camera depth pull.
            List<HeatTileLayerState> blockers = new List<HeatTileLayerState>();
            for (int i = layerIndex + 1; i < _layers.Count; i++)
                if (_layers[i].Visible && _layers[i].IsolateOpacity && _layers[i].Coverage > 0.001f)
                    blockers.Add(_layers[i]);
            List<SourceMeshBuildData> builds = BuildGeometryPerSource(state, surfaceOffset, blockers);
            if (builds.Count == 0)
            {
                Debug.LogWarning("[HeatTilePainter] No eligible triangles on " + part.partInfo.name + " for layer " + (layerIndex + 1));
                return;
            }
            int triangleCount = 0;
            int eligibleSubmeshes = 0;
            int excludedSubmeshes = 0;
            int facingRejected = 0;
            int drawPasses = 0;
            for (int sourceIndex = 0; sourceIndex < builds.Count; sourceIndex++)
            {
                SourceMeshBuildData build = builds[sourceIndex];
                ConvertBuildDataToSourceLocal(build.Source, build.Data);
                Vector2 uvRange = Vector2.one;
                Vector2 uvMinimum = Vector2.zero;
                bool normalizedUvs = HeatTileTextureLibrary.DeferredDecalAvailable;
                if (normalizedUvs) NormalizeBuildUvs(build.Data, out uvRange, out uvMinimum);
                List<int> populatedTriangleSets = FindPopulatedTriangleSets(build.Data);
                Material[] materials = CreateLayerMaterials(state, populatedTriangleSets, layerIndex);
                if (normalizedUvs)
                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                        HeatTileTextureLibrary.RestoreNormalizedUvs(materials[materialIndex], uvRange, uvMinimum);
                GameObject overlayObject = new GameObject(OverlayObjectName + "-" + layerIndex + "-" + sourceIndex);
                overlayObject.layer = build.Source.Filter.gameObject.layer;
                overlayObject.transform.SetParent(build.Source.Filter.transform, false);
                _overlayObjects.Add(overlayObject);

                Mesh mesh = new Mesh();
                mesh.name = part.partInfo.name + "-heat-tile-layer-" + layerIndex + "-source-" + sourceIndex;
                if (build.Data.Vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(build.Data.Vertices);
                mesh.SetNormals(build.Data.Normals);
                mesh.SetUVs(0, build.Data.Uvs);
                mesh.subMeshCount = populatedTriangleSets.Count;
                for (int i = 0; i < populatedTriangleSets.Count; i++)
                    mesh.SetTriangles(build.Data.TriangleSets[populatedTriangleSets[i]], i, false);
                mesh.RecalculateBounds();
                Bounds stableBounds = mesh.bounds;
                stableBounds.Expand(MaximumOverlayBoundsExpansion);
                mesh.bounds = stableBounds;
                try { mesh.RecalculateTangents(); } catch (Exception) { }
                _overlayMeshes.Add(mesh);

                overlayObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = overlayObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                _visibilityBindings.Add(new VisibilityBinding
                {
                    SourceRenderer = build.Source.Renderer,
                    OverlayRenderer = renderer
                });
                if (!HeatTileTextureLibrary.IsDeferredDecalMaterial(materials[0]))
                {
                    HeatTileOverlayCameraBias bias = overlayObject.AddComponent<HeatTileOverlayCameraBias>();
                    bias.SourceRenderer = build.Source.Renderer;
                    bias.BaseLocalPosition = overlayObject.transform.localPosition;
                }

                triangleCount += build.Data.IndexCount / 3;
                eligibleSubmeshes += build.Data.EligibleSubmeshes;
                excludedSubmeshes += build.Data.ExcludedSubmeshes;
                facingRejected += build.Data.FacingRejectedTriangles;
                drawPasses += populatedTriangleSets.Count;
            }
            Debug.Log("[HeatTilePainter] Built layer " + (layerIndex + 1) + "/" + _layers.Count + " with "
                + triangleCount + " triangles across " + builds.Count + " animated mesh sources for " + part.partInfo.name
                + ", texture=" + state.TextureId
                + ", direction=" + state.Direction + ", coverage=" + state.Coverage.ToString("F0", CultureInfo.InvariantCulture)
                + "%, mirrorMode=" + state.MirrorMode + ", isolatedBy=" + blockers.Count
                + ", edgeStyle=" + state.EdgeStyle + ", eligibleSubmeshes=" + eligibleSubmeshes
                + ", excludedSubmeshes=" + excludedSubmeshes + ", facingRejected=" + facingRejected
                + ", drawPasses=" + drawPasses);
#endif
        }

#if false // Retired overlay-only helpers; kept as source history but omitted from the assembly.
        private static List<int> FindPopulatedTriangleSets(MeshBuildData data)
        {
            List<int> result = new List<int>();
            if (data == null || data.TriangleSets == null) return result;
            for (int i = 0; i < data.TriangleSets.Length; i++)
                if (data.TriangleSets[i] != null && data.TriangleSets[i].Count > 0) result.Add(i);
            return result;
        }

        private Material[] CreateLayerMaterials(HeatTileLayerState state,
            IList<int> populatedTriangleSets, int layerIndex)
        {
            Material[] materials = new Material[populatedTriangleSets.Count];
            float opacity = Mathf.Clamp01(state.Opacity / 100f);
            for (int materialIndex = 0; materialIndex < populatedTriangleSets.Count; materialIndex++)
            {
                int triangleSet = populatedTriangleSets[materialIndex];
                Material material;
                if (triangleSet == 0)
                {
                    material = HeatTileTextureLibrary.CreateMaterial(state.TextureId, opacity, state.Tint);
                }
                else if (state.EdgeStyle == 1)
                {
                    int band = triangleSet - 1;
                    float t = (band + 0.5f) / SoftBandCount;
                    float smoothAlpha = opacity * t * t * (3f - 2f * t);
                    material = HeatTileTextureLibrary.CreateMaterial(state.TextureId, smoothAlpha, state.Tint);
                }
                else if (state.EdgeStyle == 2)
                {
                    material = HeatTileTextureLibrary.CreateMaterial("edge_gasket", opacity, Color.white);
                    HeatTileTextureLibrary.SetPatternScale(material, state.TextureId, "edge_gasket");
                }
                else
                {
                    int band = triangleSet - 1;
                    string edgeTexture = band == 0 ? "edge_gasket" : "edge_reinforced";
                    material = HeatTileTextureLibrary.CreateMaterial(edgeTexture, opacity, Color.white);
                    HeatTileTextureLibrary.SetPatternScale(material, state.TextureId, edgeTexture);
                }
                material.renderQueue += layerIndex;
                materials[materialIndex] = material;
            }
            return materials;
        }

        private List<SourceMeshBuildData> BuildGeometryPerSource(HeatTileLayerState state, float vertexOffset,
            IList<HeatTileLayerState> blockerStates)
        {
            List<SourceMeshBuildData> result = new List<SourceMeshBuildData>();
            List<MeshSource> sources = FindMeshSources();
            if (sources.Count == 0) return result;
            CoverageContext context = CreateCoverageContext(sources, state);
            if (context == null) return result;
            List<CoverageContext> blockers = new List<CoverageContext>();
            if (blockerStates != null)
                for (int i = 0; i < blockerStates.Count; i++)
                {
                    CoverageContext blocker = CreateCoverageContext(sources, blockerStates[i]);
                    if (blocker != null) blockers.Add(blocker);
                }
            float repeatSpan = Mathf.Max(0.1f, HeatTileTextureLibrary.Find(state.TextureId).PatternSpan);
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                MeshBuildData data = new MeshBuildData();
                data.TriangleSets = new List<int>[context.BlendBands + 1];
                for (int i = 0; i < data.TriangleSets.Length; i++) data.TriangleSets[i] = new List<int>(1024);
                AppendSource(sources[sourceIndex], context, blockers, repeatSpan, vertexOffset, data);
                if (data.IndexCount > 0) result.Add(new SourceMeshBuildData { Source = sources[sourceIndex], Data = data });
            }
            return result;
        }

        private void ConvertBuildDataToSourceLocal(MeshSource source, MeshBuildData data)
        {
            Matrix4x4 partToSource = source.SourceToPart.inverse;
            Matrix4x4 normalToSource = partToSource.inverse.transpose;
            for (int i = 0; i < data.Vertices.Count; i++)
            {
                data.Vertices[i] = partToSource.MultiplyPoint3x4(data.Vertices[i]);
                data.Normals[i] = normalToSource.MultiplyVector(data.Normals[i]).normalized;
            }
        }

        private static void NormalizeBuildUvs(MeshBuildData data, out Vector2 range, out Vector2 minimum)
        {
            minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < data.Uvs.Count; i++)
            {
                Vector2 uv = data.Uvs[i];
                if (uv.x < minimum.x) minimum.x = uv.x;
                if (uv.y < minimum.y) minimum.y = uv.y;
                if (uv.x > maximum.x) maximum.x = uv.x;
                if (uv.y > maximum.y) maximum.y = uv.y;
            }
            if (data.Uvs.Count == 0 || float.IsInfinity(minimum.x) || float.IsInfinity(minimum.y))
            {
                minimum = Vector2.zero;
                range = Vector2.one;
                return;
            }
            range = maximum - minimum;
            if (range.x < 0.000001f) range.x = 1f;
            if (range.y < 0.000001f) range.y = 1f;
            for (int i = 0; i < data.Uvs.Count; i++)
            {
                Vector2 uv = data.Uvs[i];
                data.Uvs[i] = new Vector2((uv.x - minimum.x) / range.x,
                    (uv.y - minimum.y) / range.y);
            }
        }
#endif

        private MeshBuildData BuildGeometry(HeatTileLayerState state, float vertexOffset)
        {
            List<MeshSource> sources = FindMeshSources();
            if (sources.Count == 0) return null;
            CoverageContext context = CreateCoverageContext(sources, state);
            if (context == null) return null;
            MeshBuildData data = new MeshBuildData();
            data.TriangleSets = new List<int>[context.BlendBands + 1];
            for (int i = 0; i < data.TriangleSets.Length; i++) data.TriangleSets[i] = new List<int>(2048);
            float repeatSpan = Mathf.Max(0.1f, HeatTileTextureLibrary.Find(state.TextureId).PatternSpan);
            for (int i = 0; i < sources.Count; i++)
                AppendSource(sources[i], context, null, repeatSpan, vertexOffset, data);
            return data;
        }

        private CoverageContext CreateCoverageContext(List<MeshSource> sources, HeatTileLayerState state)
        {
            Vector3 directionA = CoverageDirection(state);
            float minA = float.PositiveInfinity;
            float maxA = float.NegativeInfinity;
            FindHeightRange(sources, directionA, ref minA, ref maxA);
            if (float.IsInfinity(minA) || float.IsInfinity(maxA) || maxA - minA < 0.0001f) return null;
            float coverage = 1f - EffectiveCutPercent(state) / 100f;
            float clipA = Mathf.Lerp(maxA, minA, coverage);
            float blendA = Mathf.Min((maxA - minA) * state.EdgeBlend / 100f, maxA - clipA);
            CoverageContext context = new CoverageContext
            {
                State = state,
                DirectionA = directionA,
                ClipA = clipA,
                BlendA = blendA
            };
            if (state.MirrorMode != 0)
            {
                context.DirectionB = MirroredDirection(state);
                float minB = float.PositiveInfinity;
                float maxB = float.NegativeInfinity;
                FindHeightRange(sources, context.DirectionB, ref minB, ref maxB);
                if (float.IsInfinity(minB) || float.IsInfinity(maxB) || maxB - minB < 0.0001f)
                    return null;
                context.ClipB = Mathf.Lerp(maxB, minB, coverage);
                context.BlendB = Mathf.Min((maxB - minB) * state.EdgeBlend / 100f, maxB - context.ClipB);
            }
            // At full coverage the clip plane lies on the model's extreme bound, so there is no
            // paint/unpainted boundary to decorate. Omitting its otherwise invisible fade removes
            // four redundant passes from every source (100 passes on the two ten-source test gears).
            if (coverage < 0.9999f && Mathf.Max(context.BlendA, context.BlendB) > 0.0001f)
            {
                if (state.EdgeStyle == 1) context.BlendBands = SoftBandCount;
                else if (state.EdgeStyle == 2) context.BlendBands = 1;
                else if (state.EdgeStyle == 3) context.BlendBands = 2;
            }
            return context;
        }

        private List<MeshSource> FindMeshSources()
        {
            EnsureSourceCalibrations();
            List<MeshSource> liveSources = DiscoverLiveMeshSources();
            if (HighLogic.LoadedSceneIsEditor)
            {
                CaptureSourceCalibrations(liveSources);
                return liveSources;
            }
            if (_sourceCalibrations.Count == 0)
            {
                if (!_reportedCalibrationMismatch)
                {
                    _reportedCalibrationMismatch = true;
                    Debug.LogWarning("[HeatTilePainter] No editor source calibration on " + part.partInfo.name
                        + "; using live sources for backward compatibility. Visit the editor once to migrate it.");
                }
                return liveSources;
            }

            List<MeshSource> calibratedSources = new List<MeshSource>();
            HashSet<string> matchedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < liveSources.Count; i++)
            {
                MeshSource source = liveSources[i];
                SourceCalibration calibration;
                if (!_sourceCalibrations.TryGetValue(source.Key, out calibration)) continue;
                source.SourceToPart = calibration.SourceToPart;
                source.EligibleSubmeshes = CloneFlags(calibration.EligibleSubmeshes);
                calibratedSources.Add(source);
                matchedKeys.Add(source.Key);
            }
            if (!_reportedCalibrationMismatch
                && (calibratedSources.Count != liveSources.Count || matchedKeys.Count != _sourceCalibrations.Count))
            {
                _reportedCalibrationMismatch = true;
                Debug.Log("[HeatTilePainter] Enforced editor source calibration on " + part.partInfo.name
                    + ": editorSources=" + _sourceCalibrations.Count + ", flightSources=" + liveSources.Count
                    + ", matched=" + calibratedSources.Count + ". Scene-only renderers are intentionally ignored.");
            }
            return calibratedSources;
        }

        private List<MeshSource> DiscoverLiveMeshSources()
        {
            List<MeshSource> result = new List<MeshSource>();
            MeshFilter[] filters = part.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter == null || filter.sharedMesh == null || IsGeneratedObject(filter.transform)) continue;
                if (filter.GetComponentInParent<Part>() != part) continue;
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || ShouldSkipRenderer(renderer) || !renderer.enabled
                    || !renderer.gameObject.activeInHierarchy) continue;
                Mesh sourceMesh = filter.sharedMesh;
                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMesh == null || sourceMaterials == null || sourceMaterials.Length == 0) continue;
                bool[] eligibleSubmeshes = FindEligibleSubmeshes(sourceMesh, sourceMaterials);
                if (!HasEligibleSubmesh(eligibleSubmeshes)) continue;
                result.Add(new MeshSource
                {
                    Filter = filter,
                    Renderer = renderer,
                    Mesh = sourceMesh,
                    Key = CreateSourceKey(filter, sourceMesh),
                    SourceToPart = RelativeMatrix(filter.transform, part.transform),
                    EligibleSubmeshes = eligibleSubmeshes
                });
            }
            return result;
        }

        private static bool[] FindEligibleSubmeshes(Mesh mesh, MeshRenderer renderer)
        {
            return FindEligibleSubmeshes(mesh,
                renderer == null ? null : renderer.sharedMaterials);
        }

        private static bool[] FindEligibleSubmeshes(Mesh mesh, Material[] materials)
        {
            bool[] result = new bool[mesh == null ? 0 : mesh.subMeshCount];
            if (mesh == null) return result;
            if (materials == null || materials.Length == 0) return result;
            for (int subMesh = 0; subMesh < result.Length; subMesh++)
            {
                Material material = subMesh < materials.Length ? materials[subMesh] : materials[materials.Length - 1];
                result[subMesh] = !ShouldSkipMaterial(material);
            }
            return result;
        }

        private static bool HasEligibleSubmesh(bool[] flags)
        {
            if (flags == null) return false;
            for (int i = 0; i < flags.Length; i++) if (flags[i]) return true;
            return false;
        }

        private static bool[] CloneFlags(bool[] flags)
        {
            if (flags == null) return null;
            bool[] copy = new bool[flags.Length];
            Array.Copy(flags, copy, flags.Length);
            return copy;
        }

        private bool IsEligibleSubmesh(MeshSource source, int subMesh)
        {
            return source != null && source.EligibleSubmeshes != null
                && subMesh >= 0 && subMesh < source.EligibleSubmeshes.Length
                && source.EligibleSubmeshes[subMesh];
        }

        private string CreateSourceKey(MeshFilter filter)
        {
            return CreateSourceKey(filter, filter == null ? null : filter.sharedMesh);
        }

        private string CreateSourceKey(MeshFilter filter, Mesh mesh)
        {
            List<string> segments = new List<string>();
            Transform current = filter.transform;
            while (current != null && current != part.transform)
            {
                int sameNameOrdinal = 0;
                if (current.parent != null)
                    for (int i = 0; i < current.GetSiblingIndex(); i++)
                    {
                        Transform sibling = current.parent.GetChild(i);
                        if (sibling != null && sibling.name == current.name) sameNameOrdinal++;
                    }
                segments.Add(current.name + "[" + sameNameOrdinal.ToString(CultureInfo.InvariantCulture) + "]");
                current = current.parent;
            }
            segments.Reverse();
            // B9PartSwitch clones otherwise identical meshes when it reapplies a subtype in flight,
            // and Unity appends another " Instance" to the runtime mesh name. Transform path plus
            // topology is stable; the mutable mesh name is not part of renderer identity.
            return string.Join("/", segments.ToArray())
                + "|" + (mesh == null ? 0 : mesh.vertexCount).ToString(CultureInfo.InvariantCulture)
                + "|" + (mesh == null ? 0 : mesh.subMeshCount).ToString(CultureInfo.InvariantCulture);
        }

        private static string CanonicalizeSourceKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string[] fields = key.Split('|');
            // v0.12 stored path|mutable mesh name|vertex count|submesh count. Accept those craft and
            // quicksave records without requiring repainting, but immediately compare by the stable
            // v0.13 path|vertex count|submesh count identity.
            if (fields.Length >= 4)
                return fields[0] + "|" + fields[fields.Length - 2] + "|" + fields[fields.Length - 1];
            return key;
        }

        private void EnsureSourceCalibrations()
        {
            if (_sourceCalibrationsInitialized) return;
            _sourceCalibrationsInitialized = true;
            _sourceCalibrations.Clear();
            if (string.IsNullOrEmpty(sourceCalibrationData)) return;
            string[] entries = sourceCalibrationData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                try
                {
                    string[] fields = entries[entryIndex].Split('~');
                    if (fields.Length < 3) continue;
                    string key = CanonicalizeSourceKey(
                        Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])));
                    Matrix4x4 matrix;
                    if (!TryParseMatrix(fields[1], out matrix)) continue;
                    bool[] eligible = ParseSubmeshFlags(fields[2]);
                    if (!HasEligibleSubmesh(eligible)) continue;
                    _sourceCalibrations[key] = new SourceCalibration
                    {
                        Key = key,
                        SourceToPart = matrix,
                        EligibleSubmeshes = eligible
                    };
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[HeatTilePainter] Ignored invalid source calibration on "
                        + part.partInfo.name + ": " + exception.Message);
                }
            }
        }

        private void CaptureSourceCalibrations(List<MeshSource> sources)
        {
            List<string> serialized = new List<string>();
            Dictionary<string, SourceCalibration> captured =
                new Dictionary<string, SourceCalibration>(StringComparer.Ordinal);
            for (int i = 0; i < sources.Count; i++)
            {
                MeshSource source = sources[i];
                SourceCalibration calibration = new SourceCalibration
                {
                    Key = CanonicalizeSourceKey(source.Key),
                    SourceToPart = source.SourceToPart,
                    EligibleSubmeshes = CloneFlags(source.EligibleSubmeshes)
                };
                captured[calibration.Key] = calibration;
                serialized.Add(SerializeSourceCalibration(calibration));
            }
            string value = string.Join(";", serialized.ToArray());
            if (value == sourceCalibrationData) return;
            sourceCalibrationData = value;
            _sourceCalibrations.Clear();
            foreach (KeyValuePair<string, SourceCalibration> pair in captured)
                _sourceCalibrations[pair.Key] = pair.Value;
            Debug.Log("[HeatTilePainter] Captured " + captured.Count + " editor mesh source calibration(s) for "
                + part.partInfo.name + ".");
        }

        private static string SerializeSourceCalibration(SourceCalibration calibration)
        {
            string key = Convert.ToBase64String(Encoding.UTF8.GetBytes(calibration.Key ?? ""));
            return key + "~" + SerializeMatrix(calibration.SourceToPart) + "~"
                + SerializeSubmeshFlags(calibration.EligibleSubmeshes);
        }

        private static string SerializeMatrix(Matrix4x4 matrix)
        {
            string[] values = new string[16];
            for (int i = 0; i < values.Length; i++)
                values[i] = matrix[i].ToString("R", CultureInfo.InvariantCulture);
            return string.Join(",", values);
        }

        private static bool TryParseMatrix(string serialized, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            string[] values = serialized.Split(',');
            if (values.Length != 16) return false;
            for (int i = 0; i < values.Length; i++)
            {
                float parsed;
                if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return false;
                matrix[i] = parsed;
            }
            return true;
        }

        private static string SerializeSubmeshFlags(bool[] flags)
        {
            if (flags == null || flags.Length == 0) return "";
            StringBuilder builder = new StringBuilder(flags.Length);
            for (int i = 0; i < flags.Length; i++) builder.Append(flags[i] ? '1' : '0');
            return builder.ToString();
        }

        private static bool[] ParseSubmeshFlags(string serialized)
        {
            if (string.IsNullOrEmpty(serialized)) return new bool[0];
            bool[] flags = new bool[serialized.Length];
            for (int i = 0; i < serialized.Length; i++) flags[i] = serialized[i] == '1';
            return flags;
        }

        private void FindHeightRange(List<MeshSource> sources, Vector3 direction, ref float minHeight, ref float maxHeight)
        {
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                MeshSource source = sources[sourceIndex];
                Vector3[] sourceVertices;
                try { sourceVertices = source.Mesh.vertices; } catch (Exception) { continue; }
                Vector3[] partVertices = new Vector3[sourceVertices.Length];
                Matrix4x4 sourceToPart = source.SourceToPart;
                for (int i = 0; i < sourceVertices.Length; i++)
                    partVertices[i] = sourceToPart.MultiplyPoint3x4(sourceVertices[i]);
                for (int subMesh = 0; subMesh < source.Mesh.subMeshCount; subMesh++)
                {
                    if (!IsEligibleSubmesh(source, subMesh)) continue;
                    int[] triangles;
                    try { triangles = source.Mesh.GetTriangles(subMesh); } catch (Exception) { continue; }
                    for (int i = 0; i < triangles.Length; i++)
                    {
                        int index = triangles[i];
                        if (index < 0 || index >= partVertices.Length) continue;
                        float height = Vector3.Dot(partVertices[index], direction);
                        if (height < minHeight) minHeight = height;
                        if (height > maxHeight) maxHeight = height;
                    }
                }
            }
        }

        private void AppendSource(MeshSource source, CoverageContext context, IList<CoverageContext> blockers,
            float repeatSpan, float vertexOffset, MeshBuildData data)
        {
            Vector3[] sourceVertices;
            Vector3[] sourceNormals;
            try { sourceVertices = source.Mesh.vertices; sourceNormals = source.Mesh.normals; } catch (Exception) { return; }
            Vector3[] partVertices = new Vector3[sourceVertices.Length];
            Vector3[] partNormals = new Vector3[sourceVertices.Length];
            bool hasNormals = sourceNormals != null && sourceNormals.Length == sourceVertices.Length;
            Matrix4x4 sourceToPart = source.SourceToPart;
            Matrix4x4 normalToPart = sourceToPart.inverse.transpose;
            for (int i = 0; i < sourceVertices.Length; i++)
            {
                partVertices[i] = sourceToPart.MultiplyPoint3x4(sourceVertices[i]);
                if (hasNormals) partNormals[i] = normalToPart.MultiplyVector(sourceNormals[i]).normalized;
            }

            for (int subMesh = 0; subMesh < source.Mesh.subMeshCount; subMesh++)
            {
                if (!IsEligibleSubmesh(source, subMesh)) { data.ExcludedSubmeshes++; continue; }
                data.EligibleSubmeshes++;
                int[] triangles;
                try { triangles = source.Mesh.GetTriangles(subMesh); } catch (Exception) { continue; }
                for (int tri = 0; tri + 2 < triangles.Length; tri += 3)
                {
                    int ia = triangles[tri], ib = triangles[tri + 1], ic = triangles[tri + 2];
                    if (ia < 0 || ib < 0 || ic < 0 || ia >= partVertices.Length || ib >= partVertices.Length || ic >= partVertices.Length) continue;
                    Vector3 faceNormal = Vector3.Cross(partVertices[ib] - partVertices[ia], partVertices[ic] - partVertices[ia]).normalized;
                    Vector3 na = hasNormals ? partNormals[ia] : faceNormal;
                    Vector3 nb = hasNormals ? partNormals[ib] : faceNormal;
                    Vector3 nc = hasNormals ? partNormals[ic] : faceNormal;
                    Vector3 averagedNormal = (na + nb + nc).normalized;
                    if (faceNormal.sqrMagnitude < 0.5f) faceNormal = averagedNormal;
                    if (averagedNormal.sqrMagnitude > 0.5f && Vector3.Dot(faceNormal, averagedNormal) < 0f) faceNormal = -faceNormal;
                    List<ClipVertex> polygon = new List<ClipVertex>(4);
                    polygon.Add(new ClipVertex(partVertices[ia], na, 0f));
                    polygon.Add(new ClipVertex(partVertices[ib], nb, 0f));
                    polygon.Add(new ClipVertex(partVertices[ic], nc, 0f));
                    bool edgeOnly = false;
                    if (context.State.DirectionFacingOnly && !context.Mirrored
                        && context.State.EdgeStyle < 2)
                    {
                        // On high-resolution procedural noses, rejecting whole triangles by their
                        // flat face normal creates the visible saw-tooth boundary. Clip against the
                        // interpolated vertex normals instead, producing the same smooth angular
                        // contour the underlying shaded surface presents.
                        float facingThreshold = Mathf.Cos(context.State.SurfaceWrap * Mathf.Deg2Rad);
                        polygon = ClipFacing(polygon, context.DirectionA, facingThreshold);
                        if (polygon.Count < 3)
                        {
                            data.FacingRejectedTriangles++;
                            continue;
                        }
                    }
                    else if (context.State.DirectionFacingOnly && !FacesCoverage(faceNormal, context))
                    {
                        edgeOnly = context.State.EdgeStyle >= 2 && context.BlendBands > 0
                            && TriangleTouchesBoundary(partVertices[ia], partVertices[ib], partVertices[ic], context);
                        if (!edgeOnly)
                        {
                            data.FacingRejectedTriangles++;
                            continue;
                        }
                    }

                    List<List<ClipVertex>> available = new List<List<ClipVertex>> { polygon };
                    if (blockers != null)
                        for (int blockerIndex = 0; blockerIndex < blockers.Count && available.Count > 0; blockerIndex++)
                        {
                            CoverageContext blocker = blockers[blockerIndex];
                            if (blocker.State.DirectionFacingOnly && !FacesCoverage(faceNormal, blocker)) continue;
                            available = SubtractCoverage(available, blocker);
                        }
                    for (int availableIndex = 0; availableIndex < available.Count; availableIndex++)
                        AddCoveragePolygon(available[availableIndex], context, edgeOnly, repeatSpan, vertexOffset, data);
                }
            }
        }

        private void AddCoveragePolygon(List<ClipVertex> polygon, CoverageContext context, bool edgeOnly,
            float repeatSpan, float vertexOffset, MeshBuildData data)
        {
            if (polygon == null || polygon.Count < 3) return;
            if (context.BlendBands == 0)
            {
                AddShape(polygon, context, context.ClipA, context.ClipB,
                    repeatSpan, vertexOffset, data, data.TriangleSets[0]);
                return;
            }

            if (!edgeOnly)
                AddShape(polygon, context, context.ClipA + context.BlendA, context.ClipB + context.BlendB,
                    repeatSpan, vertexOffset, data, data.TriangleSets[0]);
            for (int band = 0; band < context.BlendBands; band++)
            {
                float lowA = context.ClipA + context.BlendA * band / context.BlendBands;
                float highA = context.ClipA + context.BlendA * (band + 1) / context.BlendBands;
                float lowB = context.ClipB + context.BlendB * band / context.BlendBands;
                float highB = context.ClipB + context.BlendB * (band + 1) / context.BlendBands;
                AddShapeBand(polygon, context, lowA, highA, lowB, highB,
                    repeatSpan, vertexOffset, data, data.TriangleSets[band + 1]);
            }
        }

        private void AddShape(List<ClipVertex> polygon, CoverageContext context, float thresholdA, float thresholdB,
            float repeatSpan, float vertexOffset, MeshBuildData data, List<int> triangles)
        {
            if (!context.Mirrored)
            {
                AddPolygon(ClipPlane(polygon, context.DirectionA, thresholdA, true), repeatSpan, vertexOffset, data, triangles);
                return;
            }
            if (context.State.MirrorMode == 1)
            {
                AddPolygon(ClipPlane(polygon, context.DirectionA, thresholdA, true), repeatSpan, vertexOffset, data, triangles);
                List<ClipVertex> outsideA = ClipPlane(polygon, context.DirectionA, thresholdA, false);
                AddPolygon(ClipPlane(outsideA, context.DirectionB, thresholdB, true), repeatSpan, vertexOffset, data, triangles);
            }
            else
            {
                List<ClipVertex> both = ClipPlane(polygon, context.DirectionA, thresholdA, true);
                AddPolygon(ClipPlane(both, context.DirectionB, thresholdB, true), repeatSpan, vertexOffset, data, triangles);
            }
        }

        private void AddShapeBand(List<ClipVertex> polygon, CoverageContext context,
            float lowA, float highA, float lowB, float highB,
            float repeatSpan, float vertexOffset, MeshBuildData data, List<int> triangles)
        {
            if (!context.Mirrored)
            {
                List<ClipVertex> band = ClipPlane(polygon, context.DirectionA, lowA, true);
                AddPolygon(ClipPlane(band, context.DirectionA, highA, false), repeatSpan, vertexOffset, data, triangles);
                return;
            }
            if (context.State.MirrorMode == 1)
            {
                List<ClipVertex> aBand = ClipPlane(polygon, context.DirectionA, lowA, true);
                aBand = ClipPlane(aBand, context.DirectionA, highA, false);
                aBand = ClipPlane(aBand, context.DirectionB, highB, false);
                AddPolygon(aBand, repeatSpan, vertexOffset, data, triangles);

                List<ClipVertex> bBand = ClipPlane(polygon, context.DirectionB, lowB, true);
                bBand = ClipPlane(bBand, context.DirectionB, highB, false);
                bBand = ClipPlane(bBand, context.DirectionA, lowA, false);
                AddPolygon(bBand, repeatSpan, vertexOffset, data, triangles);
            }
            else
            {
                List<ClipVertex> aBand = ClipPlane(polygon, context.DirectionA, lowA, true);
                aBand = ClipPlane(aBand, context.DirectionA, highA, false);
                aBand = ClipPlane(aBand, context.DirectionB, lowB, true);
                AddPolygon(aBand, repeatSpan, vertexOffset, data, triangles);

                List<ClipVertex> bBand = ClipPlane(polygon, context.DirectionA, highA, true);
                bBand = ClipPlane(bBand, context.DirectionB, lowB, true);
                bBand = ClipPlane(bBand, context.DirectionB, highB, false);
                AddPolygon(bBand, repeatSpan, vertexOffset, data, triangles);
            }
        }

        private static List<List<ClipVertex>> SubtractCoverage(List<List<ClipVertex>> polygons, CoverageContext blocker)
        {
            List<List<ClipVertex>> result = new List<List<ClipVertex>>();
            for (int i = 0; i < polygons.Count; i++)
            {
                List<ClipVertex> polygon = polygons[i];
                if (!blocker.Mirrored)
                {
                    AddValidPolygon(result, ClipPlane(polygon, blocker.DirectionA, blocker.ClipA, false));
                }
                else if (blocker.State.MirrorMode == 1)
                {
                    List<ClipVertex> outside = ClipPlane(polygon, blocker.DirectionA, blocker.ClipA, false);
                    AddValidPolygon(result, ClipPlane(outside, blocker.DirectionB, blocker.ClipB, false));
                }
                else
                {
                    AddValidPolygon(result, ClipPlane(polygon, blocker.DirectionA, blocker.ClipA, false));
                    List<ClipVertex> insideA = ClipPlane(polygon, blocker.DirectionA, blocker.ClipA, true);
                    AddValidPolygon(result, ClipPlane(insideA, blocker.DirectionB, blocker.ClipB, false));
                }
            }
            return result;
        }

        private static void AddValidPolygon(List<List<ClipVertex>> destination, List<ClipVertex> polygon)
        {
            if (polygon != null && polygon.Count >= 3) destination.Add(polygon);
        }

        private static bool FacesCoverage(Vector3 faceNormal, CoverageContext context)
        {
            float threshold = Mathf.Cos(context.State.SurfaceWrap * Mathf.Deg2Rad);
            if (Vector3.Dot(faceNormal, context.DirectionA) >= threshold) return true;
            return context.Mirrored && Vector3.Dot(faceNormal, context.DirectionB) >= threshold;
        }

        private static bool TriangleTouchesBoundary(Vector3 a, Vector3 b, Vector3 c, CoverageContext context)
        {
            if (TriangleTouchesPlaneBand(a, b, c, context.DirectionA, context.ClipA, context.BlendA)) return true;
            return context.Mirrored && TriangleTouchesPlaneBand(a, b, c,
                context.DirectionB, context.ClipB, context.BlendB);
        }

        private static bool TriangleTouchesPlaneBand(Vector3 a, Vector3 b, Vector3 c,
            Vector3 direction, float clip, float blend)
        {
            float ha = Vector3.Dot(a, direction);
            float hb = Vector3.Dot(b, direction);
            float hc = Vector3.Dot(c, direction);
            float minimum = Mathf.Min(ha, Mathf.Min(hb, hc));
            float maximum = Mathf.Max(ha, Mathf.Max(hb, hc));
            return maximum >= clip && minimum <= clip + blend;
        }

        private static List<ClipVertex> ClipPlane(List<ClipVertex> input, Vector3 direction,
            float clip, bool keepAbove)
        {
            List<ClipVertex> output = new List<ClipVertex>(input == null ? 4 : input.Count + 1);
            if (input == null || input.Count == 0) return output;
            ClipVertex previous = input[input.Count - 1];
            float previousHeight = Vector3.Dot(previous.Position, direction);
            bool previousInside = keepAbove ? previousHeight >= clip : previousHeight <= clip;
            for (int i = 0; i < input.Count; i++)
            {
                ClipVertex current = input[i];
                float currentHeight = Vector3.Dot(current.Position, direction);
                bool inside = keepAbove ? currentHeight >= clip : currentHeight <= clip;
                if (inside != previousInside)
                {
                    float denominator = currentHeight - previousHeight;
                    float t = Math.Abs(denominator) < 0.000001f ? 0f
                        : Mathf.Clamp01((clip - previousHeight) / denominator);
                    output.Add(new ClipVertex(Vector3.Lerp(previous.Position, current.Position, t),
                        Vector3.Lerp(previous.Normal, current.Normal, t).normalized, clip));
                }
                if (inside) output.Add(current);
                previous = current;
                previousHeight = currentHeight;
                previousInside = inside;
            }
            return output;
        }

        private static List<ClipVertex> ClipFacing(List<ClipVertex> input, Vector3 direction,
            float threshold)
        {
            List<ClipVertex> output = new List<ClipVertex>(input == null ? 4 : input.Count + 1);
            if (input == null || input.Count == 0) return output;
            ClipVertex previous = input[input.Count - 1];
            float previousValue = Vector3.Dot(previous.Normal, direction) - threshold;
            bool previousInside = previousValue >= 0f;
            for (int i = 0; i < input.Count; i++)
            {
                ClipVertex current = input[i];
                float currentValue = Vector3.Dot(current.Normal, direction) - threshold;
                bool currentInside = currentValue >= 0f;
                if (currentInside != previousInside)
                {
                    float denominator = currentValue - previousValue;
                    float t = Math.Abs(denominator) < 0.000001f ? 0f
                        : Mathf.Clamp01(-previousValue / denominator);
                    output.Add(new ClipVertex(Vector3.Lerp(previous.Position, current.Position, t),
                        Vector3.Lerp(previous.Normal, current.Normal, t).normalized,
                        Mathf.Lerp(previous.Height, current.Height, t)));
                }
                if (currentInside) output.Add(current);
                previous = current;
                previousValue = currentValue;
                previousInside = currentInside;
            }
            return output;
        }

        private void AddPolygon(List<ClipVertex> polygon, float span, float offset, MeshBuildData data, List<int> triangles)
        {
            if (polygon == null || polygon.Count < 3) return;
            for (int fan = 1; fan + 1 < polygon.Count; fan++)
                AddTriangle(polygon[0], polygon[fan], polygon[fan + 1], span, offset, data, triangles);
        }

        private static List<ClipVertex> ClipAbove(List<ClipVertex> input, float clip)
        {
            List<ClipVertex> output = new List<ClipVertex>(4);
            if (input.Count == 0) return output;
            ClipVertex previous = input[input.Count - 1];
            bool previousInside = previous.Height >= clip;
            for (int i = 0; i < input.Count; i++)
            {
                ClipVertex current = input[i];
                bool inside = current.Height >= clip;
                if (inside != previousInside) output.Add(Interpolate(previous, current, clip));
                if (inside) output.Add(current);
                previous = current;
                previousInside = inside;
            }
            return output;
        }

        private static List<ClipVertex> ClipBelow(List<ClipVertex> input, float clip)
        {
            List<ClipVertex> output = new List<ClipVertex>(4);
            if (input.Count == 0) return output;
            ClipVertex previous = input[input.Count - 1];
            bool previousInside = previous.Height <= clip;
            for (int i = 0; i < input.Count; i++)
            {
                ClipVertex current = input[i];
                bool inside = current.Height <= clip;
                if (inside != previousInside) output.Add(Interpolate(previous, current, clip));
                if (inside) output.Add(current);
                previous = current;
                previousInside = inside;
            }
            return output;
        }

        private static ClipVertex Interpolate(ClipVertex previous, ClipVertex current, float clip)
        {
            float denominator = current.Height - previous.Height;
            float t = Math.Abs(denominator) < 0.000001f ? 0f : Mathf.Clamp01((clip - previous.Height) / denominator);
            return new ClipVertex(Vector3.Lerp(previous.Position, current.Position, t),
                Vector3.Lerp(previous.Normal, current.Normal, t).normalized, clip);
        }

        private void AddTriangle(ClipVertex a, ClipVertex b, ClipVertex c, float span, float offset,
            MeshBuildData data, List<int> triangles)
        {
            Vector3 faceNormal = (a.Normal + b.Normal + c.Normal).normalized;
            if (faceNormal.sqrMagnitude < 0.5f) faceNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position).normalized;
            int index = data.Vertices.Count;
            AddVertex(a, faceNormal, span, offset, data);
            AddVertex(b, faceNormal, span, offset, data);
            AddVertex(c, faceNormal, span, offset, data);
            triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
        }

        private void AddVertex(ClipVertex vertex, Vector3 faceNormal, float span, float offset, MeshBuildData data)
        {
            Vector3 normal = vertex.Normal.sqrMagnitude > 0.5f ? vertex.Normal.normalized : faceNormal;
            data.Vertices.Add(vertex.Position + normal * offset);
            data.Normals.Add(normal);
            data.Uvs.Add(CalculateUv(vertex.Position, faceNormal, span));
        }

        private Vector2 CalculateUv(Vector3 position, Vector3 normal, float span)
        {
            Transform map = GetCraftMappingTransform();
            Vector3 p = position;
            Vector3 n = normal;
            if (part != null && part.transform != null && map != null)
            {
                Matrix4x4 partToMap = RelativeMatrix(part.transform, map);
                p = partToMap.MultiplyPoint3x4(position);
                n = partToMap.inverse.transpose.MultiplyVector(normal).normalized;
            }
            float nx = Math.Abs(n.x), ny = Math.Abs(n.y), nz = Math.Abs(n.z);
            if (ny >= nx && ny >= nz) return new Vector2(p.x / span, p.z / span);
            if (nx >= nz) return new Vector2(p.z / span, p.y / span);
            return new Vector2(p.x / span, p.y / span);
        }

        private Transform GetCraftMappingTransform()
        {
            if (part == null) return null;
            if (part.localRoot != null && part.localRoot.transform != null) return part.localRoot.transform;
            if (part.vessel != null && part.vessel.rootPart != null) return part.vessel.rootPart.transform;
            return part.transform;
        }

        private static Matrix4x4 RelativeMatrix(Transform from, Transform to)
        {
            if (from == null || to == null) return Matrix4x4.identity;
            if (from == to) return Matrix4x4.identity;

            // Compose through local transforms and a common ancestor. Unlike worldToLocal *
            // localToWorld, this never subtracts Kerbin-scale world coordinates, so coverage clips
            // and craft-space UV phase are bit-stable between the editor and flight scenes.
            Dictionary<Transform, Matrix4x4> fromToAncestor = new Dictionary<Transform, Matrix4x4>();
            Matrix4x4 fromMatrix = Matrix4x4.identity;
            Transform current = from;
            fromToAncestor[current] = fromMatrix;
            while (current.parent != null)
            {
                fromMatrix = LocalMatrix(current) * fromMatrix;
                current = current.parent;
                fromToAncestor[current] = fromMatrix;
            }

            Matrix4x4 toMatrix = Matrix4x4.identity;
            current = to;
            Matrix4x4 commonFromMatrix;
            if (fromToAncestor.TryGetValue(current, out commonFromMatrix))
                return toMatrix.inverse * commonFromMatrix;
            while (current.parent != null)
            {
                toMatrix = LocalMatrix(current) * toMatrix;
                current = current.parent;
                if (fromToAncestor.TryGetValue(current, out commonFromMatrix))
                    return toMatrix.inverse * commonFromMatrix;
            }

            // Different scene roots are not expected for part models, but preserve a safe fallback.
            return to.worldToLocalMatrix * from.localToWorldMatrix;
        }

        private static Matrix4x4 LocalMatrix(Transform transform)
        {
            return Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale);
        }

        private bool IsGeneratedObject(Transform candidate)
        {
            Transform current = candidate;
            while (current != null && current != part.transform)
            {
                if (current.name.StartsWith(GeneratedObjectPrefix, StringComparison.Ordinal)) return true;
                current = current.parent;
            }
            return false;
        }

        private static bool ShouldSkipMaterial(Material material)
        {
            if (material == null) return true;
            string materialName = material.name == null ? "" : material.name.ToLowerInvariant();
            if (materialName.Contains("window") || materialName.Contains("glass")
                || materialName.Contains("canopy") || materialName.Contains("flag")
                || materialName.Contains("overlay")) return true;
            string shaderName = material.shader != null ? material.shader.name : "";
            if (shaderName.IndexOf("transparent", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (shaderName.IndexOf("translucent", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (material.GetTag("RenderType", false, "").Equals("Transparent", StringComparison.OrdinalIgnoreCase)) return true;
            return material.renderQueue >= 3000;
        }

        private static bool ShouldSkipRenderer(MeshRenderer renderer)
        {
            if (renderer == null) return true;
            string name = renderer.name == null ? "" : renderer.name.ToLowerInvariant();
            return name.Contains("window") || name.Contains("glass") || name.Contains("canopy")
                || name.Contains("flag") || name.Contains("overlay") || name.Contains("depthmask");
        }

        private void DestroyOverlay()
        {
            ClearReforgedPaint();
        }
    }

    public sealed class HeatTileOverlayScheduler : MonoBehaviour
    {
        private static HeatTileOverlayScheduler _instance;
        private bool _sceneEventRegistered;
        private bool _editorEventRegistered;
        private bool _variantEventRegistered;

        internal static void EnsureRunning()
        {
            if (_instance != null) return;

            GameObject host = new GameObject("HeatTileOverlayScheduler");
            host.AddComponent<HeatTileOverlayScheduler>();
        }

        public void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            TryRegisterEvents();
            Debug.Log("[HeatTilePainter] Global repaint scheduler online.");
        }

        public void OnDestroy()
        {
            if (_instance != this) return;

            if (_sceneEventRegistered && GameEvents.onGameSceneLoadRequested != null)
                GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            if (_editorEventRegistered && GameEvents.onEditorShipModified != null)
                GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
            if (_variantEventRegistered && GameEvents.onVariantApplied != null)
                GameEvents.onVariantApplied.Remove(OnVariantApplied);

            _instance = null;
            ModuleHeatTileOverlay.ClearRebuildQueues();
        }

        public void Update()
        {
            TryRegisterEvents();
            if (HighLogic.LoadedSceneIsEditor || HighLogic.LoadedSceneIsFlight)
                ModuleHeatTileOverlay.PumpRebuildQueues();
        }

        private void TryRegisterEvents()
        {
            if (!_sceneEventRegistered && GameEvents.onGameSceneLoadRequested != null)
            {
                GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
                _sceneEventRegistered = true;
            }
            if (!_editorEventRegistered && GameEvents.onEditorShipModified != null)
            {
                GameEvents.onEditorShipModified.Add(OnEditorShipModified);
                _editorEventRegistered = true;
            }
            if (!_variantEventRegistered && GameEvents.onVariantApplied != null)
            {
                GameEvents.onVariantApplied.Add(OnVariantApplied);
                _variantEventRegistered = true;
            }
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            ModuleHeatTileOverlay.ClearRebuildQueues();
        }

        private void OnEditorShipModified(ShipConstruct ship)
        {
            if (!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch == null) return;
            Part selected = EditorLogic.SelectedPart;
            if (selected == null) return;
            ModuleHeatTileOverlay module = selected.FindModuleImplementing<ModuleHeatTileOverlay>();
            if (module != null) module.OnEditorShipModified(ship);
        }

        private void OnVariantApplied(Part changedPart, PartVariant variant)
        {
            if (changedPart == null) return;
            ModuleHeatTileOverlay module = changedPart.FindModuleImplementing<ModuleHeatTileOverlay>();
            if (module != null) module.OnVariantApplied(changedPart, variant);
        }
    }
}
