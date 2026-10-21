using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using EditorGizmos;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace HeatTileOverlayPrototype
{
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public sealed class HeatTilePainter : MonoBehaviour
    {
        private const string ToolLockId = "HeatTilePainter-Tool";
        private const string WindowLockId = "HeatTilePainter-Window";
        private const string PreviewObjectName = "__HeatTilePainterSurfacePreview";
        private const string EditorIconAtlasPath = "HeatTileOverlayPrototype/Assets/ui_icons";
        private const int ResetIconIndex = 6;
        private const int MoveIconIndex = 7;
        private const int RotateIconIndex = 8;
        private static readonly string[] DirectionLabels =
            { "+X Right", "-X Left", "+Y Front", "-Y Rear", "+Z Bottom", "-Z Top" };
        private static readonly string[] MirrorModeLabels = { "No mirror", "Mirror" };
        private static readonly string[] MirrorAxisLabels = { "Mirror X", "Mirror Y", "Mirror Z" };
        private static readonly string[] ToolTips =
        {
            "Off — return to normal editor selection",
            "Select — view and edit one part's layers",
            "Eyedropper — copy a complete layer stack",
            "Brush — add the current or copied paint",
            "Bucket — add paint across the craft",
            "Eraser — remove paint from a clicked part"
        };
        private static readonly string[] IconFallbackLabels =
            { "Off", "Select", "Drop", "Brush", "Fill", "Erase", "Reset", "Move", "Rotate" };
        private static readonly GUIContent[] ToolIconContents = CreateToolIconContents();
        private static readonly GUIContent ResetIconContent =
            new GUIContent("", "Reset position and direction for this layer");
        private static readonly GUIContent MoveIconContent =
            new GUIContent("", "Move this layer's coverage plane");
        private static readonly GUIContent RotateIconContent =
            new GUIContent("", "Rotate this layer's coverage direction");
        private static readonly string[] EdgeLabels = { "Hard", "Soft fade", "Gasket (fade)", "Reinforced (fade)" };
        private const ControlTypes ToolControlMask = ControlTypes.EDITOR_PAD_PICK_PLACE
            | ControlTypes.EDITOR_PAD_PICK_COPY | ControlTypes.EDITOR_GIZMO_TOOLS | ControlTypes.EDITOR_ROOT_REFLOW;

        private enum ToolMode { Off, Select, Eyedropper, Brush, Bucket, Eraser }
        private enum PlacementGizmoMode { None, Rotate, Move }

        private struct EditorIconSource
        {
            public Texture Texture;
            public Rect Uv;

            public bool IsValid { get { return Texture != null && Uv.width > 0f && Uv.height > 0f; } }
        }

        private Rect _window = new Rect(330f, 38f, 395f, 920f);
        private float _windowHeight = 920f;
        private Vector2 _scroll;
        private bool _visible;
        private ToolMode _toolMode;
        private bool _applySymmetry = true;
        private bool _skipB9Wings = true;
        private bool _showSurfacePreview = true;
        private bool _directionFacingOnly = true;
        private bool _confirmRemoveAll;
        private Part _selectedPart;
        private Part _hoverPart;
        private int _selectedLayer = -1;
        private int _hoveredLayer = -1;
        private int _dragLayer = -1;
        private int _dragTargetLayer = -1;
        private Vector2 _dragStart;
        private bool _draggingLayer;
        private bool _resizingWindow;
        private Vector2 _resizeStartMouse;
        private float _resizeStartHeight;
        private int _textureIndex;
        private int _pendingTextureIndex = -1;
        private int _direction = 3;
        private Vector3 _fineDirection = Vector3.down;
        private float _planeOffset;
        private int _mirrorMode;
        private int _pendingMirrorMode = -1;
        private int _mirrorAxis;
        private bool _isolateOpacity = true;
        private int _edgeStyle;
        private int _pendingEdgeStyle = -1;
        private int _pendingDirectionFacing = -1;
        private bool _paintLayoutSnapshotValid;
        private bool _paintLayoutFlatColor;
        private int _paintLayoutMirrorMode;
        private bool _paintLayoutDirectionFacing;
        private int _paintLayoutEdgeStyle;
        private float _coverage = 35f;
        private float _opacity = 100f;
        private float _edgeBlend = 3f;
        private float _surfaceWrap = 82f;
        private float _patternScale = 1f;
        private Color _flatColor = new Color(0.165f, 0.165f, 0.165f, 1f);
        private ApplicationLauncherButton _button;
        private GizmoRotate _directionGizmo;
        private GizmoOffset _moveGizmo;
        private PlacementGizmoMode _placementGizmoMode;
        private GameObject _directionGizmoHost;
        private Part _directionGizmoPart;
        private int _directionGizmoLayer = -1;
        private Quaternion _directionGizmoInitialLocalRotation = Quaternion.identity;
        private Vector3 _directionGizmoInitialDirection = Vector3.down;
        private float _moveGizmoInitialPlaneOffset;
        private float _moveGizmoProjectedDepth;
        private bool _directionGizmoPreviewDirty;
        private float _nextDirectionGizmoPreviewTime;
        private EditorIconSource[] _editorIcons;
        private Texture2D _editorIconAtlas;
        private bool _editorIconAtlasLoaded;
        private bool _stockMoveIconResolved;
        private bool _stockRotateIconResolved;
        private float _nextStockIconResolveTime;
        private Rect _textureBrowserWindow = new Rect(750f, 90f, 650f, 560f);
        private bool _textureBrowserOpen;
        private string _textureBrowserDirectory = "";
        private string _textureBrowserPathField = "";
        private string _textureBrowserSelectedFile = "";
        private string _textureBrowserError = "";
        private string _pendingTextureImport = "";
        private string _pendingTextureBrowserDirectory = "";
        private string _pendingTextureBrowserSelection = "";
        private bool _pendingTextureBrowserOpen;
        private Vector2 _textureBrowserScroll;
        private readonly List<string> _textureBrowserDirectories = new List<string>();
        private readonly List<string> _textureBrowserFiles = new List<string>();

        private string _previewKey = "";
        private readonly List<GameObject> _previewObjects = new List<GameObject>();
        private readonly List<Mesh> _previewMeshes = new List<Mesh>();
        private Material _previewMaterial;
        private readonly List<HeatTileLayerState> _clipboardLayers = new List<HeatTileLayerState>();
        private Part _clipboardSourcePart;
        private int _clipboardRevision;

        public void Start()
        {
            HeatTileTextureLibrary.EnsureLoaded();
            EnsureEditorIcons();
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnLauncherDestroyed);
            if (ApplicationLauncher.Instance != null) OnLauncherReady();
        }

        public void Update()
        {
            ProcessPendingTextureSelection();
            ProcessPendingConditionalSettings();
            ProcessPendingTextureBrowserNavigation();
            ProcessPendingTextureImport();
            UpdateWindowLock();
            if (!_visible)
            {
                StopDirectionGizmo();
                InputLockManager.RemoveControlLock(ToolLockId);
                SetHoverPart(null);
                ClearSurfacePreview();
                return;
            }

            ModuleHeatTileOverlay layerModule = GetOverlayModule(_selectedPart);
            if (IsDirectionGizmoActive())
            {
                if (!DirectionGizmoTargetIsValid(layerModule)) StopDirectionGizmo();
                else
                {
                    InputLockManager.RemoveControlLock(ToolLockId);
                    SetHoverPart(null);
                    UpdateDirectionGizmoPreview(layerModule);
                    return;
                }
            }
            if (_hoveredLayer >= 0 && layerModule != null && _hoveredLayer < layerModule.LayerCount)
            {
                InputLockManager.RemoveControlLock(ToolLockId);
                SetHoverPart(null);
                UpdateLayerPreview(layerModule, _hoveredLayer);
                return;
            }

            if (_toolMode == ToolMode.Off)
            {
                InputLockManager.RemoveControlLock(ToolLockId);
                SetHoverPart(null);
                ClearSurfacePreview();
                return;
            }

            InputLockManager.SetControlLock(ToolControlMask, ToolLockId);
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                SetTool(ToolMode.Off);
                return;
            }

            Part hovered = GetHoveredCraftPart();
            SetHoverPart(hovered);
            if (_showSurfacePreview) UpdateSurfacePreview(hovered);
            else ClearSurfacePreview();

            if (Input.GetMouseButtonDown(0) && !MouseInsideWindow() && hovered != null)
                PerformTool(hovered);
        }

        public void OnGUI()
        {
            if (!_visible) return;
            GUI.skin = HighLogic.Skin;
            _windowHeight = Mathf.Clamp(_windowHeight, 430f, Screen.height - 16f);
            _window.height = _windowHeight;
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, "Heat Tile Painter 0.25.0");
            _window.height = _windowHeight;
            if (_textureBrowserOpen)
            {
                _textureBrowserWindow.width = Mathf.Min(650f, Screen.width - 16f);
                _textureBrowserWindow.height = Mathf.Min(560f, Screen.height - 16f);
                _textureBrowserWindow.x = Mathf.Clamp(_textureBrowserWindow.x, 8f,
                    Mathf.Max(8f, Screen.width - _textureBrowserWindow.width - 8f));
                _textureBrowserWindow.y = Mathf.Clamp(_textureBrowserWindow.y, 8f,
                    Mathf.Max(8f, Screen.height - _textureBrowserWindow.height - 8f));
                _textureBrowserWindow = GUILayout.Window(GetInstanceID() + 137, _textureBrowserWindow,
                    DrawTextureBrowserWindow, "Add custom paint texture");
            }
        }

        public void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnLauncherDestroyed);
            InputLockManager.RemoveControlLock(ToolLockId);
            InputLockManager.RemoveControlLock(WindowLockId);
            StopDirectionGizmo();
            ClearEditorIconReferences();
            ClearSurfacePreview();
            SetHoverPart(null);
            RemoveLauncherButton();
        }

        private void OnLauncherReady()
        {
            if (_button != null || ApplicationLauncher.Instance == null) return;
            Texture icon = GameDatabase.Instance.GetTexture("HeatTileOverlayPrototype/Assets/hrsi_legacy", false);
            _button = ApplicationLauncher.Instance.AddModApplication(ShowWindow, HideWindow, null, null, null, null,
                ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH, icon);
        }

        private void OnLauncherDestroyed() { _button = null; }

        private void RemoveLauncherButton()
        {
            if (_button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
        }

        private void ShowWindow() { _visible = true; }

        private void HideWindow()
        {
            _visible = false;
            SetTool(ToolMode.Off);
            StopDirectionGizmo();
            InputLockManager.RemoveControlLock(WindowLockId);
            _selectedPart = null;
            _selectedLayer = -1;
            _textureBrowserOpen = false;
            _pendingTextureImport = "";
            _pendingTextureBrowserDirectory = "";
            _pendingTextureBrowserSelection = "";
            _pendingTextureBrowserOpen = false;
            _pendingTextureIndex = -1;
            _pendingMirrorMode = -1;
            _pendingDirectionFacing = -1;
            _pendingEdgeStyle = -1;
        }

        private void DrawWindow(int id)
        {
            DrawPinnedToolHeader();
            GUILayout.Space(5f);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            GUILayout.Label("Paint settings");
            DrawPaintSettings();
            GUILayout.EndScrollView();
            GUILayout.Space(12f);
            DrawResizeHandle();
            GUI.DragWindow(new Rect(0f, 0f, _window.width - 22f, 24f));
        }

        private void DrawPinnedToolHeader()
        {
            GUILayout.Label("Tool: choose settings, then click the craft");
            EnsureEditorIcons();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < ToolTips.Length; i++)
            {
                bool selected = DrawIconToggle((int)_toolMode == i, i, ToolIconContents[i], 42f);
                if (selected && (int)_toolMode != i) SetTool((ToolMode)i);
            }
            GUILayout.EndHorizontal();
            string hoveredTool = GUI.tooltip;
            GUILayout.Label(string.IsNullOrEmpty(hoveredTool) ? ToolHelp() : hoveredTool);
            if (_clipboardLayers.Count > 0)
            {
                GUILayout.BeginHorizontal();
                string sourceName = _clipboardSourcePart != null && _clipboardSourcePart.partInfo != null
                    ? _clipboardSourcePart.partInfo.title : "copied part";
                GUILayout.Label("Brush clipboard: " + _clipboardLayers.Count + " layer"
                    + (_clipboardLayers.Count == 1 ? "" : "s") + " from " + sourceName);
                if (GUILayout.Button("Clear", GUILayout.Width(58f))) ClearLayerClipboard();
                GUILayout.EndHorizontal();
            }
            _showSurfacePreview = GUILayout.Toggle(_showSurfacePreview, "Exact colored surface preview");
        }

        private void DrawPaintSettings()
        {
            GUILayout.Space(7f);
            GUILayout.Label("Paint");
            IList<HeatTileTextureDefinition> definitions = HeatTileTextureLibrary.Definitions;
            string[] names = new string[definitions.Count];
            for (int i = 0; i < definitions.Count; i++) names[i] = definitions[i].DisplayName;
            int previousTexture = Mathf.Clamp(_textureIndex, 0, names.Length - 1);
            HeatTileTextureDefinition displayedTexture = definitions[previousTexture];
            if (Event.current.type == EventType.Layout || !_paintLayoutSnapshotValid)
            {
                _paintLayoutSnapshotValid = true;
                _paintLayoutFlatColor = displayedTexture.IsFlatColor;
                _paintLayoutMirrorMode = _mirrorMode;
                _paintLayoutDirectionFacing = _directionFacingOnly;
                _paintLayoutEdgeStyle = _edgeStyle;
            }
            int nextTexture = GUILayout.SelectionGrid(previousTexture, names, 1);
            if (nextTexture != previousTexture) _pendingTextureIndex = nextTexture;
            if (GUILayout.Button("Browse / Add custom...")) OpenTextureBrowser();
            // Selection changes are committed from Update. Draw the current panel until the next
            // frame so Layout, input, and Repaint see the same control sequence.
            if (_paintLayoutFlatColor) DrawFlatColorControls();
            else DrawPatternScaleControls();

            GUILayout.Space(7f);
            GUILayout.Label("Side (part local)");
            DrawDirectionButtons();
            DrawFineDirectionControls();
            int nextMirrorMode = GUILayout.SelectionGrid(Mathf.Clamp(_mirrorMode, 0, 1), MirrorModeLabels, 2);
            if (nextMirrorMode != _mirrorMode) _pendingMirrorMode = nextMirrorMode;
            if (_paintLayoutMirrorMode != 0)
            {
                _mirrorAxis = GUILayout.SelectionGrid(Mathf.Clamp(_mirrorAxis, 0, 2), MirrorAxisLabels, 3);
            }
            bool nextDirectionFacing = GUILayout.Toggle(_directionFacingOnly, new GUIContent(
                "Toggle surface wrap", "Limit paint to surfaces facing the selected side"));
            if (nextDirectionFacing != _directionFacingOnly)
                _pendingDirectionFacing = nextDirectionFacing ? 1 : 0;
            if (_paintLayoutDirectionFacing)
            {
                GUILayout.Label("Surface wrap: " + _surfaceWrap.ToString("F0") + " degrees");
                _surfaceWrap = GUILayout.HorizontalSlider(_surfaceWrap, 30f, 110f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Tight 60")) _surfaceWrap = 60f;
                if (GUILayout.Button("Balanced 82")) _surfaceWrap = 82f;
                if (GUILayout.Button("Wide 100")) _surfaceWrap = 100f;
                GUILayout.EndHorizontal();
            }

            DrawPercentSlider("Coverage", ref _coverage, 0f, 100f, new[] { 0f, 25f, 50f, 100f });
            DrawPercentSlider("Opacity", ref _opacity, 0f, 100f, new[] { 25f, 50f, 75f, 100f });
            bool opacityControlEnabled = GUI.enabled;
            GUI.enabled = false;
            GUILayout.Toggle(true, new GUIContent(
                "Mask opacity", "Placed layers always mask lower layers. The legacy per-layer toggle is preserved but has no effect."));
            GUI.enabled = opacityControlEnabled;

            GUILayout.Space(6f);
            GUILayout.Label("Edge style");
            int nextEdgeStyle = GUILayout.SelectionGrid(Mathf.Clamp(_edgeStyle, 0, 3), EdgeLabels, 2);
            if (nextEdgeStyle != _edgeStyle) _pendingEdgeStyle = nextEdgeStyle;
            if (_paintLayoutEdgeStyle != 0)
            {
                float maximumWidth = _paintLayoutEdgeStyle == 1 ? 8f
                    : (_paintLayoutEdgeStyle == 2 ? 4f : 5f);
                _edgeBlend = Mathf.Clamp(_edgeBlend, 0.25f, maximumWidth);
                string widthName = "Fade width";
                GUILayout.Label(widthName + ": " + _edgeBlend.ToString("F1") + "% of part depth");
                _edgeBlend = GUILayout.HorizontalSlider(_edgeBlend, 0.25f, maximumWidth);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("0.5%")) _edgeBlend = 0.5f;
                if (GUILayout.Button("1.5%")) _edgeBlend = 1.5f;
                if (GUILayout.Button(_edgeStyle == 1 ? "4%" : "3%")) _edgeBlend = _edgeStyle == 1 ? 4f : 3f;
                GUILayout.EndHorizontal();
                GUILayout.Label(_paintLayoutEdgeStyle >= 2
                    ? "Legacy border styles render as a soft fade."
                    : " ");
            }

            GUILayout.Space(7f);
            _applySymmetry = GUILayout.Toggle(_applySymmetry, "Brush/eraser affects symmetry counterparts");
            _skipB9Wings = GUILayout.Toggle(_skipB9Wings, "Bucket skips B9 procedural wings");

            DrawLayersPanel();

            GUILayout.Space(8f);
            bool hasCraft = EditorLogic.fetch != null && EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts.Count > 0;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = hasCraft;
            string removeLabel = _confirmRemoveAll ? "Confirm: remove every paint layer" : "Remove All from Craft";
            if (GUILayout.Button(removeLabel, GUILayout.Height(28f)))
            {
                if (_confirmRemoveAll) RemoveAllFromCraft(); else _confirmRemoveAll = true;
            }
            GUI.enabled = oldEnabled;

        }

        private void DrawResizeHandle()
        {
            Rect handle = new Rect(_window.width - 22f, _windowHeight - 22f, 18f, 18f);
            GUI.Box(handle, "↕");
            Event current = Event.current;
            EventType rawType = current.rawType;
            if (rawType == EventType.MouseDown && current.button == 0 && handle.Contains(current.mousePosition))
            {
                _resizingWindow = true;
                _resizeStartMouse = current.mousePosition;
                _resizeStartHeight = _windowHeight;
                current.Use();
            }
            else if (rawType == EventType.MouseDrag && _resizingWindow)
            {
                _windowHeight = Mathf.Clamp(_resizeStartHeight + current.mousePosition.y - _resizeStartMouse.y,
                    430f, Screen.height - 16f);
                current.Use();
            }
            else if (rawType == EventType.MouseUp && current.button == 0 && _resizingWindow)
            {
                _resizingWindow = false;
                current.Use();
            }
        }

        private void EnsureEditorIcons()
        {
            if (_editorIcons == null || _editorIcons.Length != 9) _editorIcons = new EditorIconSource[9];
            if (!_editorIconAtlasLoaded)
            {
                _editorIconAtlasLoaded = true;
                _editorIconAtlas = GameDatabase.Instance.GetTexture(EditorIconAtlasPath, false);
                if (_editorIconAtlas == null)
                {
                    Debug.LogError("[HeatTilePainter] Missing editor icon atlas: " + EditorIconAtlasPath);
                }
                else
                {
                    _editorIconAtlas.wrapMode = TextureWrapMode.Clamp;
                    _editorIconAtlas.filterMode = FilterMode.Bilinear;
                    for (int i = 0; i < _editorIcons.Length; i++)
                        _editorIcons[i] = AtlasIcon(i);
                }
            }

            if ((!_stockMoveIconResolved || !_stockRotateIconResolved)
                && Time.unscaledTime >= _nextStockIconResolveTime)
            {
                _nextStockIconResolveTime = Time.unscaledTime + 1f;
                TryResolveStockEditorIcons();
            }
        }

        private void ClearEditorIconReferences()
        {
            // GameDatabase and EditorToolsUI own these textures. Release references only.
            _editorIcons = null;
            _editorIconAtlas = null;
            _editorIconAtlasLoaded = false;
            _stockMoveIconResolved = false;
            _stockRotateIconResolved = false;
            _nextStockIconResolveTime = 0f;
        }

        private EditorIconSource AtlasIcon(int index)
        {
            const float cell = 1f / 3f;
            int column = index % 3;
            int row = index / 3;
            return new EditorIconSource
            {
                Texture = _editorIconAtlas,
                Uv = new Rect(column * cell, (2 - row) * cell, cell, cell)
            };
        }

        private void TryResolveStockEditorIcons()
        {
            EditorLogic editor = EditorLogic.fetch;
            if (editor == null || editor.toolsUI == null) return;
            if (!_stockMoveIconResolved)
                _stockMoveIconResolved = TryAssignStockIcon(editor.toolsUI, "moveButton",
                    "move", "offset", MoveIconIndex);
            if (!_stockRotateIconResolved)
                _stockRotateIconResolved = TryAssignStockIcon(editor.toolsUI, "rotateButton",
                    "rotate", "rotation", RotateIconIndex);
        }

        private bool TryAssignStockIcon(EditorToolsUI tools, string fieldName, string keyword,
            string alternateKeyword, int iconIndex)
        {
            FieldInfo field = typeof(EditorToolsUI).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Toggle toggle = field == null ? null : field.GetValue(tools) as Toggle;
            Image image = FindStockToolImage(toggle, keyword, alternateKeyword);
            if (image == null || image.sprite == null || image.sprite.texture == null) return false;
            Vector4 uv = DataUtility.GetOuterUV(image.sprite);
            _editorIcons[iconIndex] = new EditorIconSource
            {
                Texture = image.sprite.texture,
                Uv = new Rect(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y)
            };
            Debug.Log("[HeatTilePainter] Reusing stock editor sprite " + image.sprite.name
                + " on " + image.gameObject.name + " for " + fieldName + ".");
            return true;
        }

        private static Image FindStockToolImage(Toggle toggle, string keyword, string alternateKeyword)
        {
            if (toggle == null) return null;
            Image[] images = toggle.GetComponentsInChildren<Image>(true);
            Image best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image == null || image.sprite == null || image.sprite.texture == null) continue;
                string identity = ((image.gameObject == null ? "" : image.gameObject.name) + " "
                    + image.sprite.name + " " + image.sprite.texture.name).ToLowerInvariant();
                int score = 0;
                if (identity.Contains(keyword)) score += 1000;
                if (identity.Contains(alternateKeyword)) score += 800;
                // A stock Toggle's root Image is normally the button frame. Prefer its child
                // glyph even when older KSP atlases give that child a generic object name.
                if (image.transform != toggle.transform) score += 1500;
                if (image != toggle.image) score += 150;
                if (image != toggle.targetGraphic) score += 75;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = image;
                }
            }
            return best;
        }

        private static GUIContent[] CreateToolIconContents()
        {
            GUIContent[] content = new GUIContent[ToolTips.Length];
            for (int i = 0; i < content.Length; i++) content[i] = new GUIContent("", ToolTips[i]);
            return content;
        }

        private bool DrawIconToggle(bool selected, int iconIndex, GUIContent content, float height)
        {
            Rect rect = GUILayoutUtility.GetRect(24f, height, GUILayout.Height(height),
                GUILayout.ExpandWidth(true));
            EditorIconSource icon = IconAt(iconIndex);
            GUIContent controlContent = icon.IsValid ? content
                : new GUIContent(IconFallbackLabels[Mathf.Clamp(iconIndex, 0, IconFallbackLabels.Length - 1)],
                    content.tooltip);
            bool next = GUI.Toggle(rect, selected, controlContent, GUI.skin.button);
            DrawEditorIcon(rect, icon);
            return next;
        }

        private bool DrawIconButton(int iconIndex, GUIContent content, float height)
        {
            Rect rect = GUILayoutUtility.GetRect(24f, height, GUILayout.Height(height),
                GUILayout.ExpandWidth(true));
            EditorIconSource icon = IconAt(iconIndex);
            GUIContent controlContent = icon.IsValid ? content
                : new GUIContent(IconFallbackLabels[Mathf.Clamp(iconIndex, 0, IconFallbackLabels.Length - 1)],
                    content.tooltip);
            bool pressed = GUI.Button(rect, controlContent, GUI.skin.button);
            DrawEditorIcon(rect, icon);
            return pressed;
        }

        private EditorIconSource IconAt(int iconIndex)
        {
            return _editorIcons != null && iconIndex >= 0 && iconIndex < _editorIcons.Length
                ? _editorIcons[iconIndex] : default(EditorIconSource);
        }

        private static void DrawEditorIcon(Rect controlRect, EditorIconSource icon)
        {
            if (!icon.IsValid || Event.current.type != EventType.Repaint) return;
            float size = Mathf.Max(1f, Mathf.Min(controlRect.width, controlRect.height) - 8f);
            Rect iconRect = new Rect(controlRect.x + (controlRect.width - size) * 0.5f,
                controlRect.y + (controlRect.height - size) * 0.5f, size, size);
            Color previous = GUI.color;
            if (!GUI.enabled) GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.45f);
            GUI.DrawTextureWithTexCoords(iconRect, icon.Texture, icon.Uv, true);
            GUI.color = previous;
        }

        private string ToolHelp()
        {
            switch (_toolMode)
            {
                case ToolMode.Select: return "Select chooses exactly which part's layers are shown. Esc disarms.";
                case ToolMode.Eyedropper: return "Eyedropper copies the clicked part's complete ordered layer stack. Esc disarms.";
                case ToolMode.Brush: return _clipboardLayers.Count > 0
                    ? "Brush stamps the copied layer stack onto the clicked part. Esc disarms."
                    : "Brush adds one layer from the current settings. Esc disarms.";
                case ToolMode.Bucket: return "Bucket adds one linked layer across the craft. Esc disarms.";
                case ToolMode.Eraser: return "Eraser removes the selected/top layer from the clicked part. Esc disarms.";
                default: return "Tools are disarmed; normal editor selection is unchanged.";
            }
        }

        private void DrawDirectionButtons()
        {
            _direction = Mathf.Clamp(_direction, 0, 5);
            for (int row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < 3; column++)
                {
                    int index = row * 3 + column;
                    bool selected = GUILayout.Toggle(_direction == index, DirectionLabels[index], GUI.skin.button);
                    if (selected && _direction != index)
                    {
                        StopDirectionGizmo();
                        _direction = index;
                        _fineDirection = ModuleHeatTileOverlay.DirectionVector(index);
                    }
                }
                GUILayout.EndHorizontal();
            }
        }

        private void DrawFineDirectionControls()
        {
            if (_fineDirection.sqrMagnitude < 0.0001f)
                _fineDirection = ModuleHeatTileOverlay.DirectionVector(_direction);
            _fineDirection.Normalize();
            if (GUILayout.Button(new GUIContent("Reset position",
                "Reset to " + DirectionLabels[_direction] + " and center the coverage plane"),
                GUILayout.Height(28f)))
            {
                StopDirectionGizmo();
                _fineDirection = ModuleHeatTileOverlay.DirectionVector(_direction);
                _planeOffset = 0f;
            }
        }

        private void DrawFlatColorControls()
        {
            GUILayout.Space(4f);
            GUILayout.Label("Flat color (RGB)");
            _flatColor.r = DrawColorChannel("R", _flatColor.r);
            _flatColor.g = DrawColorChannel("G", _flatColor.g);
            _flatColor.b = DrawColorChannel("B", _flatColor.b);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Porkjet")) SetRgb(42, 42, 42);
            if (GUILayout.Button("Stock Mk2")) SetRgb(52, 52, 52);
            if (GUILayout.Button("Airplane+")) SetRgb(30, 26, 30);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("ReStock Mk2")) SetRgb(46, 48, 50);
            if (GUILayout.Button("Charcoal")) SetRgb(46, 46, 46);
            if (GUILayout.Button("Pure black")) SetRgb(6, 6, 6);
            GUILayout.EndHorizontal();
        }

        private void DrawPatternScaleControls()
        {
            _patternScale = Mathf.Clamp(_patternScale, 0.25f, 4f);
            GUILayout.Space(4f);
            GUILayout.Label("Pattern size: " + _patternScale.ToString("0.00", CultureInfo.InvariantCulture) + "x");
            _patternScale = GUILayout.HorizontalSlider(_patternScale, 0.25f, 4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("0.5x")) _patternScale = 0.5f;
            if (GUILayout.Button("1x")) _patternScale = 1f;
            if (GUILayout.Button("2x")) _patternScale = 2f;
            if (GUILayout.Button("4x")) _patternScale = 4f;
            GUILayout.EndHorizontal();
        }

        private void OpenTextureBrowser()
        {
            StopDirectionGizmo();
            _pendingTextureBrowserOpen = true;
        }

        private void DrawTextureBrowserWindow(int id)
        {
            GUILayout.Label("Choose a PNG or JPG. Heat Tile Painter copies it into its own user library.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("GameData", GUILayout.Width(90f)))
                _pendingTextureBrowserDirectory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = !string.IsNullOrEmpty(pictures) && Directory.Exists(pictures);
            if (GUILayout.Button("Pictures", GUILayout.Width(90f))) _pendingTextureBrowserDirectory = pictures;
            GUI.enabled = oldEnabled;
            if (GUILayout.Button("Up", GUILayout.Width(70f)))
            {
                DirectoryInfo parent = string.IsNullOrEmpty(_textureBrowserDirectory)
                    ? null : Directory.GetParent(_textureBrowserDirectory);
                if (parent != null) _pendingTextureBrowserDirectory = parent.FullName;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _textureBrowserPathField = GUILayout.TextField(_textureBrowserPathField);
            if (GUILayout.Button("Go", GUILayout.Width(54f))) NavigateTextureBrowserPath();
            GUILayout.EndHorizontal();

            _textureBrowserScroll = GUILayout.BeginScrollView(_textureBrowserScroll,
                GUILayout.Height(Mathf.Max(220f, _textureBrowserWindow.height - 205f)));
            for (int i = 0; i < _textureBrowserDirectories.Count; i++)
            {
                string directory = _textureBrowserDirectories[i];
                if (GUILayout.Button("[Folder]  " + Path.GetFileName(directory)))
                    _pendingTextureBrowserDirectory = directory;
            }
            for (int i = 0; i < _textureBrowserFiles.Count; i++)
            {
                string file = _textureBrowserFiles[i];
                bool selected = string.Equals(file, _textureBrowserSelectedFile,
                    StringComparison.OrdinalIgnoreCase);
                bool next = GUILayout.Toggle(selected, Path.GetFileName(file), GUI.skin.button);
                if (next && !selected) _textureBrowserSelectedFile = file;
            }
            GUILayout.EndScrollView();

            GUILayout.Label(string.IsNullOrEmpty(_textureBrowserSelectedFile)
                ? "No image selected." : "Selected: " + Path.GetFileName(_textureBrowserSelectedFile));
            GUILayout.Label(string.IsNullOrEmpty(_textureBrowserError) ? " " : _textureBrowserError);
            GUILayout.BeginHorizontal();
            GUI.enabled = !string.IsNullOrEmpty(_textureBrowserSelectedFile)
                && File.Exists(_textureBrowserSelectedFile);
            if (GUILayout.Button("Add texture", GUILayout.Height(30f)))
            {
                _pendingTextureImport = _textureBrowserSelectedFile;
                _textureBrowserOpen = false;
            }
            GUI.enabled = oldEnabled;
            if (GUILayout.Button("Cancel", GUILayout.Height(30f))) _textureBrowserOpen = false;
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, _textureBrowserWindow.width, 24f));
        }

        private void NavigateTextureBrowserPath()
        {
            try
            {
                string candidate = Path.GetFullPath(_textureBrowserPathField);
                if (File.Exists(candidate) && HeatTileTextureLibrary.IsSupportedImagePath(candidate))
                {
                    _pendingTextureBrowserDirectory = Path.GetDirectoryName(candidate);
                    _pendingTextureBrowserSelection = candidate;
                    return;
                }
                _pendingTextureBrowserDirectory = candidate;
            }
            catch { _textureBrowserError = "That path could not be opened."; }
        }

        private void SetTextureBrowserDirectory(string directory)
        {
            try
            {
                string full = Path.GetFullPath(directory);
                if (!Directory.Exists(full))
                {
                    _textureBrowserError = "That folder does not exist.";
                    return;
                }
                _textureBrowserDirectory = full;
                _textureBrowserPathField = full;
                _textureBrowserSelectedFile = "";
                _textureBrowserError = "";
                RefreshTextureBrowserDirectory();
            }
            catch { _textureBrowserError = "That folder could not be opened."; }
        }

        private void RefreshTextureBrowserDirectory()
        {
            _textureBrowserDirectories.Clear();
            _textureBrowserFiles.Clear();
            try
            {
                string[] directories = Directory.GetDirectories(_textureBrowserDirectory);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                _textureBrowserDirectories.AddRange(directories);
                string[] files = Directory.GetFiles(_textureBrowserDirectory);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                    if (HeatTileTextureLibrary.IsSupportedImagePath(files[i]))
                        _textureBrowserFiles.Add(files[i]);
            }
            catch
            {
                _textureBrowserError = "KSP cannot read that folder.";
            }
            _textureBrowserScroll = Vector2.zero;
        }

        private void ProcessPendingTextureImport()
        {
            if (string.IsNullOrEmpty(_pendingTextureImport)) return;
            string source = _pendingTextureImport;
            _pendingTextureImport = "";
            HeatTileTextureDefinition definition;
            string error;
            if (HeatTileTextureLibrary.TryImport(source, out definition, out error))
            {
                _textureIndex = HeatTileTextureLibrary.FindIndex(definition.Id);
                _textureBrowserError = "";
                ClearSurfacePreview();
                ScreenMessages.PostScreenMessage("Added custom paint texture " + definition.DisplayName + ".",
                    2f, ScreenMessageStyle.UPPER_CENTER);
            }
            else
            {
                _textureBrowserError = error;
                _textureBrowserOpen = true;
            }
        }

        private void ProcessPendingTextureSelection()
        {
            if (_pendingTextureIndex < 0) return;
            IList<HeatTileTextureDefinition> definitions = HeatTileTextureLibrary.Definitions;
            _textureIndex = Mathf.Clamp(_pendingTextureIndex, 0, definitions.Count - 1);
            _pendingTextureIndex = -1;
            if (definitions[_textureIndex].IsFlatColor)
                _flatColor = definitions[_textureIndex].DefaultColor;
            ClearSurfacePreview();
        }

        private void ProcessPendingConditionalSettings()
        {
            if (_pendingMirrorMode >= 0)
            {
                _mirrorMode = Mathf.Clamp(_pendingMirrorMode, 0, 1);
                _pendingMirrorMode = -1;
            }
            if (_pendingDirectionFacing >= 0)
            {
                _directionFacingOnly = _pendingDirectionFacing != 0;
                _pendingDirectionFacing = -1;
            }
            if (_pendingEdgeStyle >= 0)
            {
                _edgeStyle = Mathf.Clamp(_pendingEdgeStyle, 0, 3);
                _pendingEdgeStyle = -1;
            }
        }

        private void ProcessPendingTextureBrowserNavigation()
        {
            if (_pendingTextureBrowserOpen)
            {
                _pendingTextureBrowserOpen = false;
                _textureBrowserOpen = true;
                _textureBrowserError = "";
                _textureBrowserWindow.x = Mathf.Max(8f, (Screen.width - _textureBrowserWindow.width) * 0.5f);
                _textureBrowserWindow.y = Mathf.Max(8f, (Screen.height - _textureBrowserWindow.height) * 0.5f);
                if (string.IsNullOrEmpty(_textureBrowserDirectory))
                    SetTextureBrowserDirectory(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"));
                else RefreshTextureBrowserDirectory();
            }
            if (string.IsNullOrEmpty(_pendingTextureBrowserDirectory)) return;
            string directory = _pendingTextureBrowserDirectory;
            _pendingTextureBrowserDirectory = "";
            SetTextureBrowserDirectory(directory);
            if (!string.IsNullOrEmpty(_pendingTextureBrowserSelection))
            {
                _textureBrowserSelectedFile = _pendingTextureBrowserSelection;
                _pendingTextureBrowserSelection = "";
            }
        }

        private void SetRgb(int red, int green, int blue)
        {
            _flatColor = new Color(red / 255f, green / 255f, blue / 255f, 1f);
        }

        private static float DrawColorChannel(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + " " + Mathf.RoundToInt(value * 255f), GUILayout.Width(48f));
            value = GUILayout.HorizontalSlider(value, 0f, 1f);
            GUILayout.EndHorizontal();
            return Mathf.Clamp01(value);
        }

        private static void DrawPercentSlider(string label, ref float value, float minimum, float maximum, float[] presets)
        {
            GUILayout.Space(6f);
            GUILayout.Label(label + ": " + value.ToString("F0") + "%");
            value = GUILayout.HorizontalSlider(value, minimum, maximum);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < presets.Length; i++)
            {
                if (GUILayout.Button(presets[i].ToString("F0") + "%")) value = presets[i];
            }
            GUILayout.EndHorizontal();
        }

        private void DrawLayersPanel()
        {
            GUILayout.Space(10f);
            string selectedName = _selectedPart == null ? "none yet" : _selectedPart.partInfo.title;
            GUILayout.Label("Layers on: " + selectedName);
            ModuleHeatTileOverlay module = GetOverlayModule(_selectedPart);
            if (module == null)
            {
                GUILayout.Label("Click with a tool to choose a part.");
                _hoveredLayer = -1;
                return;
            }
            if (module.LayerCount == 0)
            {
                GUILayout.Label("No paint layers.");
                _hoveredLayer = -1;
                return;
            }
            _selectedLayer = Mathf.Clamp(_selectedLayer, 0, module.LayerCount - 1);
            Event currentEvent = Event.current;
            EventType eventType = currentEvent.rawType;
            int hoveredThisPass = -1;
            int nearestDragTarget = -1;
            float nearestDragDistance = float.PositiveInfinity;
            bool consumeMouseDown = false;
            for (int index = module.LayerCount - 1; index >= 0; index--)
            {
                HeatTileLayerState layer = module.GetLayer(index);
                Rect row = GUILayoutUtility.GetRect(10f, 44f, GUILayout.ExpandWidth(true));
                GUI.Box(row, "", LayerRowStyle(_selectedLayer == index));

                Rect visibleRect = new Rect(row.x + 5f, row.y + 11f, 22f, 22f);
                bool visible = GUI.Toggle(visibleRect, layer.Visible, "");
                if (visible != layer.Visible) ModifyLayerVisibility(module, index, visible);

                Rect thumbnailRect = new Rect(row.x + 30f, row.y + 4f, 52f, 36f);
                DrawLayerThumbnail(thumbnailRect, layer);
                string label = "L" + (index + 1) + "  " + HeatTileTextureLibrary.DisplayName(layer.TextureId)
                    + "\n" + DirectionSummary(layer) + "  " + layer.Coverage.ToString("F0") + "%  "
                    + EdgeLabels[Mathf.Clamp(layer.EdgeStyle, 0, 3)]
                    + "  " + layer.PatternScale.ToString("0.##", CultureInfo.InvariantCulture) + "x"
                    + (layer.IsolateOpacity ? "  isolated" : "");
                Rect labelRect = new Rect(row.x + 88f, row.y + 3f, row.width - 116f, 38f);
                GUI.Label(labelRect, label);
                Rect handleRect = new Rect(row.xMax - 25f, row.y + 8f, 20f, 28f);
                GUI.Label(handleRect, "|||");

                bool overRow = row.Contains(currentEvent.mousePosition);
                if (overRow) hoveredThisPass = index;
                bool overDragArea = overRow && !visibleRect.Contains(currentEvent.mousePosition);
                if (eventType == EventType.MouseDown && currentEvent.button == 0 && overDragArea)
                {
                    if (_selectedLayer != index) StopDirectionGizmo();
                    _selectedLayer = index;
                    _dragLayer = index;
                    _dragTargetLayer = index;
                    _dragStart = currentEvent.mousePosition;
                    _draggingLayer = false;
                    consumeMouseDown = true;
                }

                if (_dragLayer >= 0 && (eventType == EventType.MouseDrag || eventType == EventType.MouseUp))
                {
                    float distance = Mathf.Abs(currentEvent.mousePosition.y - row.center.y);
                    if (distance < nearestDragDistance)
                    {
                        nearestDragDistance = distance;
                        nearestDragTarget = index;
                    }
                }

                if (eventType == EventType.Repaint && _draggingLayer && _dragTargetLayer == index)
                {
                    Color previous = GUI.color;
                    GUI.color = new Color(0.15f, 0.85f, 1f, 0.85f);
                    GUI.DrawTexture(new Rect(row.x + 2f, row.y + 1f, row.width - 4f, 3f), Texture2D.whiteTexture);
                    GUI.color = previous;
                }
            }

            if (consumeMouseDown && currentEvent.type != EventType.Used) currentEvent.Use();
            if (eventType == EventType.MouseDrag && _dragLayer >= 0)
            {
                if ((currentEvent.mousePosition - _dragStart).sqrMagnitude > 16f) _draggingLayer = true;
                if (nearestDragTarget >= 0) _dragTargetLayer = nearestDragTarget;
                if (currentEvent.type != EventType.Used) currentEvent.Use();
            }
            if (eventType == EventType.Repaint || eventType == EventType.MouseMove || eventType == EventType.MouseDrag)
                _hoveredLayer = hoveredThisPass;
            if (eventType == EventType.MouseUp && currentEvent.button == 0 && _dragLayer >= 0)
            {
                if (nearestDragTarget >= 0) _dragTargetLayer = nearestDragTarget;
                if (_draggingLayer && _dragTargetLayer >= 0 && _dragTargetLayer != _dragLayer && EditorLogic.fetch != null)
                {
                    BeginPaintUndo();
                    if (module.MoveLayer(_dragLayer, _dragTargetLayer - _dragLayer))
                    {
                        _selectedLayer = _dragTargetLayer;
                        EndPaintUndo();
                    }
                }
                _dragLayer = -1;
                _dragTargetLayer = -1;
                _draggingLayer = false;
                if (currentEvent.type != EventType.Used) currentEvent.Use();
            }

            GUILayout.BeginHorizontal();
            bool oldEnabled = GUI.enabled;
            GUI.enabled = _selectedLayer >= 0;
            if (GUILayout.Button("Load all")) LoadLayer(module, _selectedLayer);
            if (GUILayout.Button("Copy")) CopyLayerToClipboard(module, _selectedLayer);
            if (GUILayout.Button("Update")) UpdateSelectedLayer(module);
            if (GUILayout.Button("Delete")) DeleteSelectedLayer(module);
            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
            DrawThermalProtection(module);
            bool editingPlacement = DirectionGizmoTargetIsValid(module);
            bool rotating = editingPlacement && _placementGizmoMode == PlacementGizmoMode.Rotate;
            bool moving = editingPlacement && _placementGizmoMode == PlacementGizmoMode.Move;
            GUILayout.BeginHorizontal();
            if (DrawIconButton(ResetIconIndex, ResetIconContent, 34f))
                ResetSelectedPlacement(module);
            bool nextMove = DrawIconToggle(moving, MoveIconIndex, MoveIconContent, 34f);
            if (nextMove != moving)
            {
                if (nextMove) StartMoveGizmo(module, _selectedLayer);
                else StopDirectionGizmo();
            }
            bool nextRotate = DrawIconToggle(rotating, RotateIconIndex, RotateIconContent, 34f);
            if (nextRotate != rotating)
            {
                if (nextRotate) StartDirectionGizmo(module, _selectedLayer);
                else StopDirectionGizmo();
            }
            GUILayout.EndHorizontal();
            if (editingPlacement)
            {
                string snap;
                if (_placementGizmoMode == PlacementGizmoMode.Rotate && _directionGizmo != null)
                    snap = _directionGizmo.useAngleSnap
                        ? "Rotate snap " + _directionGizmo.SnapDegrees.ToString("F1", CultureInfo.InvariantCulture) + " degrees"
                        : "Smooth rotation";
                else if (_placementGizmoMode == PlacementGizmoMode.Move && _moveGizmo != null)
                    snap = _moveGizmo.useGrid
                        ? "Move snap " + _moveGizmo.SnapInterval.ToString("F2", CultureInfo.InvariantCulture) + " m"
                        : "Smooth movement";
                else snap = "Placement gizmo";
                GUILayout.Label(snap + ". C and Editor Extensions use the editor's current settings.");
            }
            else
            {
                string placementTip = GUI.tooltip;
                if (!string.IsNullOrEmpty(placementTip)) GUILayout.Label(placementTip);
            }
            GUILayout.Label("Hover previews one layer. Drag a row/≡ handle to reorder it.");
        }

        private void DrawThermalProtection(ModuleHeatTileOverlay module)
        {
            GUILayout.Space(6f);
            bool enabled = module.ThermalProtectionEnabled;
            bool next = GUILayout.Toggle(enabled,
                "Functional heat protection");
            if (next != enabled) SetThermalProtection(module, next);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && next;
            float amount = module.ThermalProtectionAmount;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Less", GUILayout.Width(52f))) SetThermalAmount(module, amount - 0.25f);
            GUILayout.Label("Coating: " + amount.ToString("0.##") + "x");
            if (GUILayout.Button("More", GUILayout.Width(52f))) SetThermalAmount(module, amount + 0.25f);
            if (GUILayout.Button("1x", GUILayout.Width(36f))) SetThermalAmount(module, 1f);
            GUILayout.EndHorizontal();
            GUILayout.Label(string.Format("{0:0} K internal / {1:0} K skin; +{2:0.##}% dry mass",
                module.ProtectedInternalLimit, module.ProtectedSkinLimit, module.ThermalMassPercent));
            GUI.enabled = wasEnabled;
            GUILayout.Label("0.25x–4x coating. Raises limits; does not consume ablator.");
        }

        private static GUIStyle LayerRowStyle(bool selected)
        {
            GUIStyle style = new GUIStyle(GUI.skin.button);
            if (!selected) return style;
            style.normal.background = GUI.skin.button.onNormal.background;
            style.normal.textColor = GUI.skin.button.onNormal.textColor;
            style.hover.background = GUI.skin.button.onHover.background;
            style.hover.textColor = GUI.skin.button.onHover.textColor;
            style.active.background = GUI.skin.button.onActive.background;
            style.active.textColor = GUI.skin.button.onActive.textColor;
            return style;
        }

        private static void DrawLayerThumbnail(Rect rect, HeatTileLayerState layer)
        {
            GUI.Box(rect, "");
            Rect imageRect = new Rect(rect.x + 2f, rect.y + 2f, rect.width - 4f, rect.height - 4f);
            HeatTileTextureDefinition definition;
            bool definitionFound = HeatTileTextureLibrary.FindExact(layer.TextureId, out definition);
            Color previousColor = GUI.color;
            Texture texture = Texture2D.whiteTexture;
            if (!definitionFound) GUI.color = new Color(0.3f, 0.08f, 0.08f, 1f);
            else if (definition.IsFlatColor) GUI.color = new Color(layer.Tint.r, layer.Tint.g, layer.Tint.b, 1f);
            else
            {
                Texture2D loaded;
                HeatTileTextureLibrary.TryResolveTexture(definition.Id, false, out loaded);
                if (loaded != null) texture = loaded;
            }
            GUI.DrawTexture(imageRect, texture, ScaleMode.ScaleAndCrop, false);
            GUI.color = previousColor;
            string badge = layer.EdgeStyle == 0 ? "H" : (layer.EdgeStyle == 1 ? "F" : (layer.EdgeStyle == 2 ? "G" : "R"));
            GUI.Box(new Rect(rect.xMax - 17f, rect.yMax - 17f, 15f, 15f), badge);
        }

        private void SetTool(ToolMode tool)
        {
            if (tool != ToolMode.Off) StopDirectionGizmo();
            _toolMode = tool;
            _confirmRemoveAll = false;
            ClearSurfacePreview();
            if (tool == ToolMode.Off)
            {
                InputLockManager.RemoveControlLock(ToolLockId);
                SetHoverPart(null);
            }
        }

        private bool IsDirectionGizmoActive()
        {
            bool hasGizmo = _placementGizmoMode == PlacementGizmoMode.Rotate
                ? _directionGizmo != null : (_placementGizmoMode == PlacementGizmoMode.Move
                    && _moveGizmo != null);
            return hasGizmo && _directionGizmoHost != null
                && _directionGizmoPart != null && _directionGizmoLayer >= 0;
        }

        private bool DirectionGizmoTargetIsValid(ModuleHeatTileOverlay module)
        {
            return IsDirectionGizmoActive() && module != null && module.part == _directionGizmoPart
                && _selectedPart == _directionGizmoPart && _selectedLayer == _directionGizmoLayer
                && _directionGizmoLayer < module.LayerCount;
        }

        private void StartDirectionGizmo(ModuleHeatTileOverlay module, int index)
        {
            StopDirectionGizmo();
            if (module == null || module.part == null || index < 0 || index >= module.LayerCount
                || EditorLogic.fetch == null) return;
            HeatTileLayerState layer = module.GetLayer(index);
            if (layer == null) return;

            SetTool(ToolMode.Off);
            _selectedPart = module.part;
            _selectedLayer = index;
            _hoveredLayer = -1;
            _direction = layer.Direction;
            _fineDirection = ModuleHeatTileOverlay.CoverageDirection(layer);
            _mirrorMode = Mathf.Clamp(layer.MirrorMode, 0, 1);
            _mirrorAxis = layer.MirrorAxis;
            _planeOffset = layer.PlaneOffset;
            _directionGizmoPart = module.part;
            _directionGizmoLayer = index;
            _placementGizmoMode = PlacementGizmoMode.Rotate;

            _directionGizmoHost = new GameObject("__HeatTilePainterDirectionGizmoHost");
            _directionGizmoHost.hideFlags = HideFlags.HideAndDontSave;
            _directionGizmoHost.transform.SetParent(module.part.transform, false);
            _directionGizmoHost.transform.localRotation = Quaternion.FromToRotation(Vector3.forward,
                _fineDirection.normalized);
            RepositionDirectionGizmo(module, layer);

            Camera camera = EditorLogic.fetch.editorCamera != null
                ? EditorLogic.fetch.editorCamera : Camera.main;
            try
            {
                _directionGizmo = GizmoRotate.Attach(_directionGizmoHost.transform,
                    _directionGizmoHost.transform.position, Quaternion.identity,
                    OnDirectionGizmoRotate, OnDirectionGizmoRotated, camera);
                if (_directionGizmo == null) throw new InvalidOperationException("stock rotate gizmo prefab was unavailable");
                // Paint directions are persisted in part-local space. The stock gizmo still owns
                // snap mode and snap increments, including changes made by Editor Extensions.
                _directionGizmo.SetCoordSystem(Space.Self);
                _directionGizmoInitialLocalRotation = Quaternion.Inverse(module.part.transform.rotation)
                    * _directionGizmo.transform.rotation;
                _directionGizmoInitialDirection = _fineDirection.normalized;
                _directionGizmoPreviewDirty = true;
                _nextDirectionGizmoPreviewTime = 0f;
            }
            catch (Exception exception)
            {
                Debug.LogError("[HeatTilePainter] Could not attach the stock direction gizmo: " + exception.Message);
                ScreenMessages.PostScreenMessage("Heat Tile Painter: stock rotate gizmo unavailable.",
                    2f, ScreenMessageStyle.UPPER_CENTER);
                StopDirectionGizmo();
            }
        }

        private void StartMoveGizmo(ModuleHeatTileOverlay module, int index)
        {
            StopDirectionGizmo();
            if (module == null || module.part == null || index < 0 || index >= module.LayerCount
                || EditorLogic.fetch == null) return;
            HeatTileLayerState layer = module.GetLayer(index);
            if (layer == null) return;

            SetTool(ToolMode.Off);
            _selectedPart = module.part;
            _selectedLayer = index;
            _hoveredLayer = -1;
            _direction = layer.Direction;
            _fineDirection = ModuleHeatTileOverlay.CoverageDirection(layer);
            _mirrorMode = Mathf.Clamp(layer.MirrorMode, 0, 1);
            _mirrorAxis = layer.MirrorAxis;
            _planeOffset = layer.PlaneOffset;
            _directionGizmoPart = module.part;
            _directionGizmoLayer = index;
            _placementGizmoMode = PlacementGizmoMode.Move;
            _moveGizmoInitialPlaneOffset = layer.PlaneOffset;

            Vector3 localPlane;
            if (!module.TryGetCoveragePlane(layer, out localPlane, out _moveGizmoProjectedDepth))
            {
                ScreenMessages.PostScreenMessage("Heat Tile Painter: coverage plane is unavailable on this geometry.",
                    2f, ScreenMessageStyle.UPPER_CENTER);
                StopDirectionGizmo();
                return;
            }

            _directionGizmoHost = new GameObject("__HeatTilePainterMoveGizmoHost");
            _directionGizmoHost.hideFlags = HideFlags.HideAndDontSave;
            _directionGizmoHost.transform.SetParent(module.part.transform, false);
            _directionGizmoHost.transform.localRotation = Quaternion.identity;
            _directionGizmoHost.transform.localPosition = localPlane;

            Camera camera = EditorLogic.fetch.editorCamera != null
                ? EditorLogic.fetch.editorCamera : Camera.main;
            try
            {
                _moveGizmo = GizmoOffset.Attach(_directionGizmoHost.transform, Quaternion.identity,
                    OnMoveGizmoMove, OnMoveGizmoMoved, camera);
                if (_moveGizmo == null) throw new InvalidOperationException("stock move gizmo prefab was unavailable");
                _moveGizmo.SetCoordSystem(Space.Self);
                _directionGizmoPreviewDirty = true;
                _nextDirectionGizmoPreviewTime = 0f;
            }
            catch (Exception exception)
            {
                Debug.LogError("[HeatTilePainter] Could not attach the stock move gizmo: " + exception.Message);
                ScreenMessages.PostScreenMessage("Heat Tile Painter: stock move gizmo unavailable.",
                    2f, ScreenMessageStyle.UPPER_CENTER);
                StopDirectionGizmo();
            }
        }

        private void OnMoveGizmoMove(Vector3 worldOffset)
        {
            if (!IsDirectionGizmoActive() || _placementGizmoMode != PlacementGizmoMode.Move
                || _moveGizmoProjectedDepth < 0.0001f) return;
            Vector3 localOffset = _directionGizmoPart.transform.InverseTransformVector(worldOffset);
            float projected = Vector3.Dot(localOffset, _fineDirection.normalized);
            _planeOffset = Mathf.Clamp(_moveGizmoInitialPlaneOffset
                + projected / _moveGizmoProjectedDepth * 100f, -100f, 100f);
            _directionGizmoPreviewDirty = true;
        }

        private void OnMoveGizmoMoved(Vector3 worldOffset)
        {
            OnMoveGizmoMove(worldOffset);
            ModuleHeatTileOverlay module = GetOverlayModule(_directionGizmoPart);
            if (DirectionGizmoTargetIsValid(module)) CommitDirectionGizmo(module, true);
        }

        private void OnDirectionGizmoRotate(Quaternion ignoredDelta)
        {
            if (!IsDirectionGizmoActive()) return;
            Quaternion currentLocal = Quaternion.Inverse(_directionGizmoPart.transform.rotation)
                * _directionGizmo.transform.rotation;
            Quaternion localDelta = currentLocal * Quaternion.Inverse(_directionGizmoInitialLocalRotation);
            Vector3 next = localDelta * _directionGizmoInitialDirection;
            if (next.sqrMagnitude < 0.0001f) return;
            _fineDirection = next.normalized;
            _direction = ClosestDirection(_fineDirection);
            _directionGizmoPreviewDirty = true;
        }

        private void OnDirectionGizmoRotated(Quaternion ignoredDelta)
        {
            OnDirectionGizmoRotate(ignoredDelta);
            ModuleHeatTileOverlay module = GetOverlayModule(_directionGizmoPart);
            if (DirectionGizmoTargetIsValid(module)) CommitDirectionGizmo(module, true);
        }

        private void CommitDirectionGizmo(ModuleHeatTileOverlay module, bool createBackup)
        {
            if (!DirectionGizmoTargetIsValid(module) || EditorLogic.fetch == null) return;
            HeatTileLayerState layer = module.GetLayer(_directionGizmoLayer);
            if (layer == null) return;
            layer.Direction = ClosestDirection(_fineDirection);
            layer.CustomDirection = _fineDirection.normalized;
            layer.MirrorMode = Mathf.Clamp(_mirrorMode, 0, 1);
            layer.MirrorAxis = Mathf.Clamp(_mirrorAxis, 0, 2);
            layer.PlaneOffset = _planeOffset;
            if (createBackup) BeginPaintUndo();
            if (module.UpdateLayer(_directionGizmoLayer, layer))
            {
                if (createBackup) EndPaintUndo();
                else GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
                _directionGizmoPreviewDirty = true;
                if (_placementGizmoMode == PlacementGizmoMode.Rotate)
                    RepositionDirectionGizmo(module, layer);
            }
        }

        private void RepositionDirectionGizmo(ModuleHeatTileOverlay module, HeatTileLayerState layer)
        {
            if (_directionGizmoHost == null || module == null || module.part == null) return;
            Bounds paintBounds;
            Vector3 localPivot = Vector3.zero;
            if (module.TryGetPreviewBounds(layer, out paintBounds))
            {
                Vector3 direction = ModuleHeatTileOverlay.CoverageDirection(layer);
                Vector3 extents = paintBounds.extents;
                float outward = Mathf.Abs(direction.x) * extents.x + Mathf.Abs(direction.y) * extents.y
                    + Mathf.Abs(direction.z) * extents.z;
                localPivot = paintBounds.center + direction * (outward + 0.08f);
            }
            _directionGizmoHost.transform.localPosition = localPivot;
            if (_directionGizmo != null) _directionGizmo.transform.position = _directionGizmoHost.transform.position;
        }

        private void UpdateDirectionGizmoPreview(ModuleHeatTileOverlay module)
        {
            if (!_directionGizmoPreviewDirty || Time.realtimeSinceStartup < _nextDirectionGizmoPreviewTime) return;
            HeatTileLayerState layer = module.GetLayer(_directionGizmoLayer);
            if (layer == null) return;
            layer.Direction = ClosestDirection(_fineDirection);
            layer.CustomDirection = _fineDirection.normalized;
            layer.MirrorMode = Mathf.Clamp(_mirrorMode, 0, 1);
            layer.MirrorAxis = Mathf.Clamp(_mirrorAxis, 0, 2);
            layer.PlaneOffset = _planeOffset;
            ClearSurfacePreview();
            _previewKey = "gizmo:" + module.part.GetInstanceID() + ":" + _directionGizmoLayer + ":"
                + _fineDirection.x.ToString("F4", CultureInfo.InvariantCulture) + ":"
                + _fineDirection.y.ToString("F4", CultureInfo.InvariantCulture) + ":"
                + _fineDirection.z.ToString("F4", CultureInfo.InvariantCulture) + ":"
                + _mirrorMode + ":" + _mirrorAxis + ":"
                + _planeOffset.ToString("F3", CultureInfo.InvariantCulture);
            _previewMaterial = CreatePreviewMaterial();
            if (_previewMaterial != null)
                AddPreviewObject(module.part, module, layer, new Color(1f, 0.68f, 0.08f, 0.20f));
            _directionGizmoPreviewDirty = false;
            // Large procedural meshes make exact previews expensive. Eight visual updates per
            // second keeps dragging responsive while the stock gizmo itself remains full-rate.
            _nextDirectionGizmoPreviewTime = Time.realtimeSinceStartup + 0.125f;
        }

        private void StopDirectionGizmo()
        {
            if (_directionGizmo != null)
            {
                try { _directionGizmo.Detach(); }
                catch (Exception exception)
                {
                    Debug.LogWarning("[HeatTilePainter] Direction gizmo detach failed: " + exception.Message);
                }
            }
            _directionGizmo = null;
            if (_moveGizmo != null)
            {
                try { _moveGizmo.Detach(); }
                catch (Exception exception)
                {
                    Debug.LogWarning("[HeatTilePainter] Move gizmo detach failed: " + exception.Message);
                }
            }
            _moveGizmo = null;
            _placementGizmoMode = PlacementGizmoMode.None;
            if (_directionGizmoHost != null) Destroy(_directionGizmoHost);
            _directionGizmoHost = null;
            _directionGizmoPart = null;
            _directionGizmoLayer = -1;
            _directionGizmoPreviewDirty = false;
            ClearSurfacePreview();
        }

        private void PerformTool(Part clickedPart)
        {
            if (clickedPart == null || EditorLogic.fetch == null) return;
            if (_toolMode == ToolMode.Select)
            {
                SelectPart(clickedPart);
                ScreenMessages.PostScreenMessage("Viewing layers on " + clickedPart.partInfo.title + ".",
                    1.25f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            if (_toolMode == ToolMode.Eyedropper)
            {
                SelectPart(clickedPart);
                CopyWholeStackToClipboard(clickedPart);
                return;
            }
            BeginPaintUndo();
            int changed = 0;
            if (_toolMode == ToolMode.Brush) changed = ApplyBrush(clickedPart);
            else if (_toolMode == ToolMode.Bucket) changed = ApplyBucket(clickedPart);
            else if (_toolMode == ToolMode.Eraser) changed = ApplyEraser(clickedPart);
            SelectPart(clickedPart);
            ClearSurfacePreview();
            if (changed > 0)
            {
                EndPaintUndo();
                string verb = _toolMode == ToolMode.Eraser ? "Removed paint from " : "Added paint to ";
                ScreenMessages.PostScreenMessage(verb + changed + " part" + (changed == 1 ? "." : "s."),
                    1.5f, ScreenMessageStyle.UPPER_CENTER);
            }
            else ScreenMessages.PostScreenMessage("Heat Tile Painter: nothing changed.", 1.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private void SelectPart(Part selected)
        {
            StopDirectionGizmo();
            _selectedPart = selected;
            ModuleHeatTileOverlay module = GetOverlayModule(selected);
            _selectedLayer = module == null ? -1 : module.LayerCount - 1;
            _hoveredLayer = -1;
            ClearSurfacePreview();
        }

        private void ClearLayerClipboard()
        {
            _clipboardLayers.Clear();
            _clipboardSourcePart = null;
            _clipboardRevision++;
            ClearSurfacePreview();
        }

        private void CopyWholeStackToClipboard(Part selected)
        {
            ModuleHeatTileOverlay module = GetOverlayModule(selected);
            if (module == null || module.LayerCount == 0)
            {
                ScreenMessages.PostScreenMessage("Eyedropper: that part has no paint layers.",
                    1.5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            _clipboardLayers.Clear();
            for (int i = 0; i < module.LayerCount; i++)
            {
                HeatTileLayerState layer = module.GetLayer(i);
                if (layer != null) _clipboardLayers.Add(layer);
            }
            _clipboardSourcePart = selected;
            _clipboardRevision++;
            ClearSurfacePreview();
            ScreenMessages.PostScreenMessage("Copied " + _clipboardLayers.Count + "-layer stack from "
                + PartTitle(selected) + ".", 1.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private void CopyLayerToClipboard(ModuleHeatTileOverlay module, int index)
        {
            if (module == null || index < 0 || index >= module.LayerCount) return;
            HeatTileLayerState layer = module.GetLayer(index);
            if (layer == null) return;

            _clipboardLayers.Clear();
            _clipboardLayers.Add(layer);
            _clipboardSourcePart = module.part;
            _clipboardRevision++;
            ClearSurfacePreview();
            ScreenMessages.PostScreenMessage("Copied layer " + (index + 1) + " from "
                + PartTitle(module.part) + ".", 1.5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private int ApplyBrush(Part clickedPart)
        {
            List<Part> targets = new List<Part>();
            targets.Add(clickedPart);
            if (_applySymmetry) targets.AddRange(clickedPart.symmetryCounterparts);
            if (_clipboardLayers.Count > 0) return AddClipboardToTargets(targets);
            return AddLayerToTargets(clickedPart, targets, false);
        }

        private int AddClipboardToTargets(IList<Part> targets)
        {
            HashSet<Part> visited = new HashSet<Part>();
            int changed = 0;
            int skipped = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Part target = targets[i];
                if (target == null || !visited.Add(target)) continue;
                if (ShouldProtectPart(target)) { skipped++; continue; }
                ModuleHeatTileOverlay module = GetOverlayModule(target);
                if (module == null) { skipped++; continue; }

                for (int layerIndex = 0; layerIndex < _clipboardLayers.Count; layerIndex++)
                    module.AddLayer(ClipboardStateForTarget(_clipboardLayers[layerIndex], target));
                changed++;
            }
            Debug.Log("[HeatTilePainter] Clipboard brush changed=" + changed + ", skipped=" + skipped
                + ", layersPerPart=" + _clipboardLayers.Count);
            return changed;
        }

        private HeatTileLayerState ClipboardStateForTarget(HeatTileLayerState copied, Part target)
        {
            HeatTileLayerState state = copied.Clone();
            if (_clipboardSourcePart != null && target != null)
            {
                Vector3 worldDirection = _clipboardSourcePart.transform.localToWorldMatrix.MultiplyVector(
                    ModuleHeatTileOverlay.CoverageDirection(copied)).normalized;
                Vector3 targetDirection = target.transform.worldToLocalMatrix.MultiplyVector(worldDirection).normalized;
                state.Direction = ClosestDirection(targetDirection);
                state.CustomDirection = targetDirection;
                if (copied.MirrorMode != 0)
                {
                    Vector3 worldAxis = _clipboardSourcePart.transform.localToWorldMatrix.MultiplyVector(
                        AxisVector(copied.MirrorAxis)).normalized;
                    state.MirrorAxis = ClosestAxis(target.transform.worldToLocalMatrix.MultiplyVector(worldAxis));
                }
            }
            return state;
        }

        private int ApplyBucket(Part clickedPart)
        {
            if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return 0;
            return AddLayerToTargets(clickedPart, EditorLogic.fetch.ship.parts, true);
        }

        private int AddLayerToTargets(Part directionReference, IList<Part> targets, bool bucket)
        {
            Vector3 worldDirection = directionReference.transform.localToWorldMatrix.MultiplyVector(
                _fineDirection.normalized).normalized;
            Vector3 worldMirrorAxis = directionReference.transform.localToWorldMatrix.MultiplyVector(
                AxisVector(_mirrorAxis)).normalized;
            HashSet<Part> visited = new HashSet<Part>();
            int changed = 0;
            int skipped = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Part target = targets[i];
                if (target == null || !visited.Add(target)) continue;
                if (ShouldProtectPart(target) || (bucket && _skipB9Wings && IsB9ProceduralWing(target))) { skipped++; continue; }
                ModuleHeatTileOverlay module = GetOverlayModule(target);
                if (module == null) { skipped++; continue; }
                Vector3 targetDirection = target.transform.worldToLocalMatrix.MultiplyVector(worldDirection).normalized;
                HeatTileLayerState state = CurrentSettings();
                state.Direction = ClosestDirection(targetDirection);
                state.CustomDirection = targetDirection;
                if (state.MirrorMode != 0)
                    state.MirrorAxis = ClosestAxis(target.transform.worldToLocalMatrix.MultiplyVector(worldMirrorAxis));
                module.AddLayer(state);
                changed++;
            }
            Debug.Log("[HeatTilePainter] " + (bucket ? "Bucket" : "Brush") + " changed=" + changed + ", skipped=" + skipped);
            return changed;
        }

        private int ApplyEraser(Part clickedPart)
        {
            List<Part> targets = new List<Part>();
            targets.Add(clickedPart);
            if (_applySymmetry) targets.AddRange(clickedPart.symmetryCounterparts);
            HashSet<Part> visited = new HashSet<Part>();
            int changed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Part target = targets[i];
                if (target == null || !visited.Add(target)) continue;
                ModuleHeatTileOverlay module = GetOverlayModule(target);
                if (module == null || module.LayerCount == 0) continue;
                int removeIndex = target == _selectedPart && _selectedLayer >= 0 && _selectedLayer < module.LayerCount
                    ? _selectedLayer : module.LayerCount - 1;
                if (module.RemoveLayer(removeIndex)) changed++;
            }
            return changed;
        }

        private HeatTileLayerState CurrentSettings()
        {
            IList<HeatTileTextureDefinition> definitions = HeatTileTextureLibrary.Definitions;
            HeatTileTextureDefinition texture = definitions[Mathf.Clamp(_textureIndex, 0, definitions.Count - 1)];
            return new HeatTileLayerState
            {
                Visible = true,
                TextureId = texture.Id,
                Direction = _direction,
                CustomDirection = _fineDirection.normalized,
                Coverage = _coverage,
                Opacity = _opacity,
                EdgeBlend = _edgeStyle == 0 ? 0f : _edgeBlend,
                EdgeStyle = _edgeStyle,
                DirectionFacingOnly = _directionFacingOnly,
                SurfaceWrap = _surfaceWrap,
                Tint = _flatColor,
                MirrorMode = Mathf.Clamp(_mirrorMode, 0, 1),
                MirrorAxis = _mirrorAxis,
                IsolateOpacity = _isolateOpacity,
                PlaneOffset = _planeOffset,
                PatternScale = _patternScale
            };
        }

        private void LoadLayer(ModuleHeatTileOverlay module, int index)
        {
            StopDirectionGizmo();
            HeatTileLayerState layer = module.GetLayer(index);
            if (layer == null) return;
            _pendingTextureIndex = HeatTileTextureLibrary.FindIndex(layer.TextureId);
            _direction = layer.Direction;
            _fineDirection = ModuleHeatTileOverlay.CoverageDirection(layer);
            _coverage = layer.Coverage;
            _opacity = layer.Opacity;
            _edgeBlend = layer.EdgeBlend;
            _pendingEdgeStyle = layer.EdgeStyle;
            _pendingDirectionFacing = layer.DirectionFacingOnly ? 1 : 0;
            _surfaceWrap = layer.SurfaceWrap;
            _flatColor = layer.Tint;
            _pendingMirrorMode = Mathf.Clamp(layer.MirrorMode, 0, 1);
            _mirrorAxis = layer.MirrorAxis;
            _isolateOpacity = layer.IsolateOpacity;
            _planeOffset = layer.PlaneOffset;
            _patternScale = layer.PatternScale;
            ClearSurfacePreview();
        }

        private static System.Reflection.FieldInfo _invertedUndoField;
        private static bool _undoCompatibilityChecked;

        private static bool UsesInvertedUndo()
        {
            if (!_undoCompatibilityChecked)
            {
                _undoCompatibilityChecked = true;
                foreach (AssemblyLoader.LoadedAssembly assembly in AssemblyLoader.loadedAssemblies)
                {
                    Type type = assembly.assembly.GetType("KSPCommunityFixes.QoL.BetterEditorUndoRedo", false);
                    if (type == null) continue;
                    _invertedUndoField = type.GetField("editorPatched",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    break;
                }
            }
            return _invertedUndoField != null && (bool)_invertedUndoField.GetValue(null);
        }

        private static void BeginPaintUndo()
        {
            // KSPCF captures before editing, then saves redo state when Undo is requested.
            // Stock KSP captures after editing. Detect the applied patch, not just installation.
            if (UsesInvertedUndo()) EditorLogic.fetch.SetBackup();
        }

        private static void EndPaintUndo()
        {
            if (UsesInvertedUndo()) GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            else EditorLogic.fetch.SetBackup();
        }

        private void UpdateSelectedLayer(ModuleHeatTileOverlay module)
        {
            if (_selectedLayer < 0 || _selectedLayer >= module.LayerCount || EditorLogic.fetch == null) return;
            StopDirectionGizmo();
            BeginPaintUndo();
            if (module.UpdateLayer(_selectedLayer, CurrentSettings()))
            {
                EndPaintUndo();
                ClearSurfacePreview();
            }
        }

        private void ModifyLayerVisibility(ModuleHeatTileOverlay module, int index, bool visible)
        {
            if (EditorLogic.fetch == null) return;
            BeginPaintUndo();
            if (module.SetLayerVisible(index, visible)) EndPaintUndo();
        }

        private void SetThermalProtection(ModuleHeatTileOverlay module, bool enabled)
        {
            if (module == null || module.part == null || EditorLogic.fetch == null) return;
            if (module.ThermalProtectionEnabled == enabled && !_applySymmetry) return;
            BeginPaintUndo();
            List<Part> targets = new List<Part>();
            targets.Add(module.part);
            if (_applySymmetry) targets.AddRange(module.part.symmetryCounterparts);
            HashSet<Part> visited = new HashSet<Part>();
            bool changed = false;
            for (int i = 0; i < targets.Count; i++)
            {
                Part target = targets[i];
                if (target == null || !visited.Add(target)) continue;
                ModuleHeatTileOverlay targetModule = GetOverlayModule(target);
                if (targetModule != null && targetModule.SetThermalProtection(enabled)) changed = true;
            }
            if (changed) EndPaintUndo();
        }

        private void SetThermalAmount(ModuleHeatTileOverlay module, float amount)
        {
            if (module == null || module.part == null || EditorLogic.fetch == null) return;
            amount = ThermalProtectionPolicy.Amount(amount);
            if (module.ThermalProtectionAmount == amount) return;
            BeginPaintUndo();
            HashSet<Part> targets = new HashSet<Part>();
            targets.Add(module.part);
            if (_applySymmetry)
                foreach (Part counterpart in module.part.symmetryCounterparts) targets.Add(counterpart);
            bool changed = false;
            foreach (Part target in targets)
            {
                ModuleHeatTileOverlay targetModule = GetOverlayModule(target);
                if (targetModule != null && targetModule.SetThermalProtectionAmount(amount)) changed = true;
            }
            if (changed) EndPaintUndo();
        }

        private void ResetSelectedPlacement(ModuleHeatTileOverlay module)
        {
            if (module == null || _selectedLayer < 0 || _selectedLayer >= module.LayerCount
                || EditorLogic.fetch == null) return;
            StopDirectionGizmo();
            HeatTileLayerState layer = module.GetLayer(_selectedLayer);
            if (layer == null) return;
            layer.CustomDirection = ModuleHeatTileOverlay.DirectionVector(layer.Direction);
            layer.PlaneOffset = 0f;
            _direction = layer.Direction;
            _fineDirection = layer.CustomDirection;
            _planeOffset = 0f;
            BeginPaintUndo();
            if (module.UpdateLayer(_selectedLayer, layer))
            {
                EndPaintUndo();
                ClearSurfacePreview();
            }
        }

        private void MoveSelectedLayer(ModuleHeatTileOverlay module, int delta)
        {
            if (EditorLogic.fetch == null) return;
            BeginPaintUndo();
            if (module.MoveLayer(_selectedLayer, delta))
            {
                _selectedLayer += delta;
                EndPaintUndo();
            }
        }

        private void DeleteSelectedLayer(ModuleHeatTileOverlay module)
        {
            if (EditorLogic.fetch == null) return;
            StopDirectionGizmo();
            BeginPaintUndo();
            if (module.RemoveLayer(_selectedLayer))
            {
                _selectedLayer = Mathf.Min(_selectedLayer, module.LayerCount - 1);
                EndPaintUndo();
                ClearSurfacePreview();
            }
        }

        private void RemoveAllFromCraft()
        {
            StopDirectionGizmo();
            _confirmRemoveAll = false;
            if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return;
            BeginPaintUndo();
            int changed = 0;
            for (int i = 0; i < EditorLogic.fetch.ship.parts.Count; i++)
            {
                ModuleHeatTileOverlay module = GetOverlayModule(EditorLogic.fetch.ship.parts[i]);
                if (module != null && module.RemoveAllLayers()) changed++;
            }
            if (changed > 0) EndPaintUndo();
            _selectedLayer = -1;
            ClearSurfacePreview();
            ScreenMessages.PostScreenMessage("Removed all heat-tile paint from " + changed + " part" + (changed == 1 ? "." : "s."),
                2f, ScreenMessageStyle.UPPER_CENTER);
        }

        private void UpdateSurfacePreview(Part hovered)
        {
            if (hovered == null) { ClearSurfacePreview(); return; }
            string key = PreviewSettingsKey(hovered);
            if (key == _previewKey) return;
            ClearSurfacePreview();
            _previewKey = key;

            Color color;
            if (_toolMode == ToolMode.Eraser) color = new Color(1f, 0.12f, 0.12f, 0.32f);
            else if (_toolMode == ToolMode.Bucket) color = new Color(0.18f, 0.48f, 1f, 0.25f);
            else if (_toolMode == ToolMode.Select) color = new Color(0.12f, 0.85f, 1f, 0.22f);
            else if (_toolMode == ToolMode.Eyedropper) color = new Color(0.82f, 0.34f, 1f, 0.24f);
            else color = new Color(0.10f, 0.92f, 0.82f, 0.28f);
            _previewMaterial = CreatePreviewMaterial();
            if (_previewMaterial == null) return;

            List<Part> targets = PreviewTargets(hovered);
            Vector3 worldDirection = hovered.transform.localToWorldMatrix.MultiplyVector(
                _fineDirection.normalized).normalized;
            Vector3 worldMirrorAxis = hovered.transform.localToWorldMatrix.MultiplyVector(
                AxisVector(_mirrorAxis)).normalized;
            HashSet<Part> visited = new HashSet<Part>();
            for (int i = 0; i < targets.Count; i++)
            {
                Part target = targets[i];
                if (target == null || !visited.Add(target) || ShouldProtectPart(target)) continue;
                if (_toolMode == ToolMode.Bucket && _skipB9Wings && IsB9ProceduralWing(target)) continue;
                ModuleHeatTileOverlay module = GetOverlayModule(target);
                if (module == null) continue;

                if (_toolMode == ToolMode.Brush && _clipboardLayers.Count > 0)
                {
                    for (int layerIndex = 0; layerIndex < _clipboardLayers.Count; layerIndex++)
                    {
                        HeatTileLayerState copied = ClipboardStateForTarget(_clipboardLayers[layerIndex], target);
                        if (copied.Visible) AddPreviewObject(target, module, copied, color);
                    }
                    continue;
                }

                HeatTileLayerState state;
                if (_toolMode == ToolMode.Eraser)
                {
                    int index = target == _selectedPart && _selectedLayer >= 0 && _selectedLayer < module.LayerCount
                        ? _selectedLayer : module.LayerCount - 1;
                    state = module.GetLayer(index);
                    if (state == null) continue;
                }
                else if (_toolMode == ToolMode.Select || _toolMode == ToolMode.Eyedropper)
                {
                    state = CurrentSettings();
                    state.Coverage = 100f;
                    state.EdgeBlend = 0f;
                    state.EdgeStyle = 0;
                    state.DirectionFacingOnly = false;
                }
                else
                {
                    state = CurrentSettings();
                    Vector3 localDirection = target.transform.worldToLocalMatrix.MultiplyVector(worldDirection).normalized;
                    state.Direction = ClosestDirection(localDirection);
                    state.CustomDirection = localDirection;
                    if (state.MirrorMode != 0)
                        state.MirrorAxis = ClosestAxis(target.transform.worldToLocalMatrix.MultiplyVector(worldMirrorAxis));
                }
                AddPreviewObject(target, module, state, color);
            }
        }

        private void UpdateLayerPreview(ModuleHeatTileOverlay module, int index)
        {
            if (module == null || module.part == null) { ClearSurfacePreview(); return; }
            string key = "layer:" + module.part.GetInstanceID() + ":" + index + ":" + module.Revision;
            if (key == _previewKey) return;
            ClearSurfacePreview();
            _previewKey = key;
            _previewMaterial = CreatePreviewMaterial();
            if (_previewMaterial == null) return;
            HeatTileLayerState layer = module.GetLayer(index);
            if (layer == null) return;
            AddPreviewObject(module.part, module, layer, new Color(1f, 0.72f, 0.10f, 0.28f));
        }

        private void AddPreviewObject(Part target, ModuleHeatTileOverlay module, HeatTileLayerState state, Color color)
        {
            Mesh mesh = module.CreatePreviewMesh(state, color);
            if (mesh == null) return;
            GameObject preview = new GameObject(PreviewObjectName);
            preview.layer = target.gameObject.layer;
            preview.transform.SetParent(target.transform, false);
            preview.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = preview.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _previewMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _previewObjects.Add(preview);
            _previewMeshes.Add(mesh);
        }

        private List<Part> PreviewTargets(Part hovered)
        {
            List<Part> targets = new List<Part>();
            if (_toolMode == ToolMode.Bucket && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                targets.AddRange(EditorLogic.fetch.ship.parts);
            else
            {
                targets.Add(hovered);
                if ((_toolMode == ToolMode.Brush || _toolMode == ToolMode.Eraser) && _applySymmetry)
                    targets.AddRange(hovered.symmetryCounterparts);
            }
            return targets;
        }

        private string PreviewSettingsKey(Part hovered)
        {
            int revisionSum = 0;
            if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                for (int i = 0; i < EditorLogic.fetch.ship.parts.Count; i++)
                {
                    ModuleHeatTileOverlay module = GetOverlayModule(EditorLogic.fetch.ship.parts[i]);
                    if (module != null) revisionSum = unchecked(revisionSum * 31 + module.Revision);
                }
            return hovered.GetInstanceID() + ":" + (int)_toolMode + ":" + _textureIndex + ":" + _direction + ":"
                + _fineDirection.x.ToString("F4", CultureInfo.InvariantCulture) + ":"
                + _fineDirection.y.ToString("F4", CultureInfo.InvariantCulture) + ":"
                + _fineDirection.z.ToString("F4", CultureInfo.InvariantCulture) + ":"
                + _coverage.ToString("F2", CultureInfo.InvariantCulture) + ":" + _opacity.ToString("F2", CultureInfo.InvariantCulture)
                + ":" + _edgeBlend.ToString("F2", CultureInfo.InvariantCulture) + ":" + _edgeStyle + ":"
                + _surfaceWrap.ToString("F1", CultureInfo.InvariantCulture) + ":" + _directionFacingOnly + ":"
                + _mirrorMode + ":" + _mirrorAxis + ":" + _isolateOpacity + ":"
                + _planeOffset.ToString("F2", CultureInfo.InvariantCulture) + ":"
                + _applySymmetry + ":" + _skipB9Wings + ":" + _clipboardRevision + ":" + revisionSum;
        }

        private static Material CreatePreviewMaterial()
        {
            Shader shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            if (shader == null) return null;
            Material material = new Material(shader);
            material.name = "Heat Tile Painter exact surface preview";
            material.mainTexture = Texture2D.whiteTexture;
            Color white = Color.white;
            if (material.HasProperty("_Color")) material.SetColor("_Color", white);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", white);
            if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Off);
            material.renderQueue = 4000;
            return material;
        }

        private void ClearSurfacePreview()
        {
            for (int i = 0; i < _previewObjects.Count; i++) if (_previewObjects[i] != null) Destroy(_previewObjects[i]);
            _previewObjects.Clear();
            for (int i = 0; i < _previewMeshes.Count; i++) if (_previewMeshes[i] != null) Destroy(_previewMeshes[i]);
            _previewMeshes.Clear();
            if (_previewMaterial != null) Destroy(_previewMaterial);
            _previewMaterial = null;
            _previewKey = "";
        }

        private Part GetHoveredCraftPart()
        {
            if (MouseInsideWindow() || EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return null;
            Part hovered = Mouse.HoveredPart;
            if (hovered != null && EditorLogic.fetch.ship.Contains(hovered)) return hovered;
            if (Camera.main == null) return null;
            RaycastHit[] hits = Physics.RaycastAll(Camera.main.ScreenPointToRay(Input.mousePosition));
            Array.Sort(hits, delegate(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); });
            for (int i = 0; i < hits.Length; i++)
            {
                Part candidate = hits[i].transform.GetComponentInParent<Part>();
                if (candidate != null && EditorLogic.fetch.ship.Contains(candidate)) return candidate;
            }
            return null;
        }

        private void SetHoverPart(Part part)
        {
            if (_hoverPart == part) return;
            _hoverPart = part;
            if (_showSurfacePreview) ClearSurfacePreview();
        }

        private bool MouseInsideWindow()
        {
            Vector2 guiPosition = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return _window.Contains(guiPosition)
                || (_textureBrowserOpen && _textureBrowserWindow.Contains(guiPosition));
        }

        private void UpdateWindowLock()
        {
            if (_visible && MouseInsideWindow()) InputLockManager.SetControlLock(ControlTypes.EDITOR_LOCK, WindowLockId);
            else InputLockManager.RemoveControlLock(WindowLockId);
        }

        private static ModuleHeatTileOverlay GetOverlayModule(Part part)
        {
            return part == null ? null : part.FindModuleImplementing<ModuleHeatTileOverlay>();
        }

        private static string PartTitle(Part part)
        {
            if (part == null) return "part";
            return part.partInfo != null && !string.IsNullOrEmpty(part.partInfo.title) ? part.partInfo.title : part.name;
        }

        private static bool ShouldProtectPart(Part candidate)
        {
            if (candidate == null) return true;
            for (int i = 0; i < candidate.Modules.Count; i++)
            {
                PartModule module = candidate.Modules[i];
                if (module != null && module.GetType().Name.StartsWith("ModuleConformal", StringComparison.OrdinalIgnoreCase)) return true;
            }
            string name = candidate.partInfo != null ? candidate.partInfo.name : candidate.name;
            return !string.IsNullOrEmpty(name) && name.StartsWith("conformaldecals", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsB9ProceduralWing(Part candidate)
        {
            if (candidate == null) return false;
            string name = candidate.partInfo != null ? candidate.partInfo.name : candidate.name;
            if (!string.IsNullOrEmpty(name) && name.StartsWith("B9.Aero.Wing.Procedural", StringComparison.OrdinalIgnoreCase)) return true;
            for (int i = 0; i < candidate.Modules.Count; i++)
            {
                PartModule module = candidate.Modules[i];
                if (module != null && module.GetType().Name.IndexOf("WingProcedural", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static int ClosestDirection(Vector3 localDirection)
        {
            int bestIndex = 0;
            float bestDot = float.NegativeInfinity;
            for (int i = 0; i < 6; i++)
            {
                float dot = Vector3.Dot(localDirection, ModuleHeatTileOverlay.DirectionVector(i));
                if (dot > bestDot) { bestDot = dot; bestIndex = i; }
            }
            return bestIndex;
        }

        private static Vector3 AxisVector(int axis)
        {
            return axis == 0 ? Vector3.right : (axis == 1 ? Vector3.up : Vector3.forward);
        }

        private static int ClosestAxis(Vector3 localAxis)
        {
            localAxis = new Vector3(Mathf.Abs(localAxis.x), Mathf.Abs(localAxis.y), Mathf.Abs(localAxis.z));
            if (localAxis.x >= localAxis.y && localAxis.x >= localAxis.z) return 0;
            return localAxis.y >= localAxis.z ? 1 : 2;
        }

        private static string DirectionSummary(HeatTileLayerState layer)
        {
            Vector3 direction = ModuleHeatTileOverlay.CoverageDirection(layer);
            string summary = DirectionLabels[Mathf.Clamp(layer.Direction, 0, 5)];
            if (Vector3.Dot(direction, ModuleHeatTileOverlay.DirectionVector(layer.Direction)) < 0.9995f)
                summary += " fine";
            if (layer.MirrorMode != 0)
                summary += " +M";
            if (Mathf.Abs(layer.PlaneOffset) > 0.05f)
                summary += " @" + layer.PlaneOffset.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
            return summary;
        }

    }
}
