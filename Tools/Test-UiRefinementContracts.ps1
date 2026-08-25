$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$painterPath = Join-Path $projectRoot 'Source\HeatTilePainter.cs'
$modulePath = Join-Path $projectRoot 'Source\ModuleHeatTileOverlay.cs'
$bridgePath = Join-Path $projectRoot 'Source\ReforgedPaintBridge.cs'
$configPath = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\HeatTileOverlayPrototype.cfg'
$iconPath = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Assets\ui_icons.png'
$buildPath = Join-Path $projectRoot 'build.ps1'
$assemblyPath = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'
. (Join-Path $projectRoot 'Tools\Resolve-KspManaged.ps1')
$managed = Get-KspManagedPath

$painter = Get-Content -Raw -LiteralPath $painterPath
$module = Get-Content -Raw -LiteralPath $modulePath
$bridge = Get-Content -Raw -LiteralPath $bridgePath
$config = Get-Content -Raw -LiteralPath $configPath
$build = Get-Content -Raw -LiteralPath $buildPath

$requiredUi = @(
    '"+X Right", "-X Left", "+Y Front", "-Y Rear", "+Z Bottom", "-Z Top"'
    'GUILayout.Label("Side (part local)")'
    '"Toggle surface wrap"'
    '"Mask opacity"'
    'new GUIContent("Reset position"'
    'private bool _skipB9Wings = true;'
    'private bool _isolateOpacity = true;'
    'private int _edgeStyle;'
    'private static readonly string[] MirrorModeLabels = { "No mirror", "Mirror" };'
)
foreach ($contract in $requiredUi) {
    if (-not $painter.Contains($contract)) { throw "Missing UI refinement contract: $contract" }
}

foreach ($retiredText in @('Coverage direction (part-local)', 'Only direction-facing surfaces',
    'Isolate opacity at overlaps', 'Starts along local (', 'Mirror intersect',
    'Apply mirror settings')) {
    if ($painter.Contains($retiredText)) { throw "Retired UI text/control remains: $retiredText" }
}
if (-not $config.Contains('displayName = Flat Color') -or $config.Contains('displayName = Porkjet Matte Color')) {
    throw 'The flat-color paint display name is not refined.'
}
if (-not $painter.Contains('Mathf.Clamp(_mirrorMode, 0, 1), MirrorModeLabels, 2') -or
    -not $module.Contains('state.MirrorMode = state.MirrorMode == 0 ? 0 : 1;') -or
    $bridge.Contains('_reportedMirrorIntersectionFallback')) {
    throw 'Mirror controls or legacy normalization still expose unsupported intersection behavior.'
}

foreach ($iconContract in @('EditorIconAtlasPath = "HeatTileOverlayPrototype/Assets/ui_icons";',
    'TryAssignStockIcon(editor.toolsUI, "moveButton"', 'TryAssignStockIcon(editor.toolsUI, "rotateButton"',
    'GUI.DrawTextureWithTexCoords(', 'DrawIconButton(ResetIconIndex',
    'DrawIconToggle(moving, MoveIconIndex', 'DrawIconToggle(rotating, RotateIconIndex')) {
    if (-not $painter.Contains($iconContract)) { throw "Missing icon contract: $iconContract" }
}
if ($painter.Contains('CreateEditorIcon') -or $painter.Contains('SetPixels32(') -or
    $painter.Contains('DrawIconArc(') -or $painter.Contains('FillIconPolygon(')) {
    throw 'The rejected procedural icon generator remains reachable.'
}
if (-not $build.Contains("UnityEngine.UIModule.dll")) {
    throw 'The build does not explicitly reference the stock Unity UI module.'
}
if (-not (Test-Path -LiteralPath $iconPath)) { throw "Generated icon atlas is missing: $iconPath" }

Add-Type -AssemblyName System.Drawing
$iconBitmap = [System.Drawing.Bitmap]::FromFile($iconPath)
try {
    if ($iconBitmap.Width -ne $iconBitmap.Height -or $iconBitmap.Width % 3 -ne 0 -or
        -not $iconBitmap.PixelFormat.ToString().Contains('Argb')) {
        throw "Icon atlas must be a square, alpha-enabled 3x3 image; got $($iconBitmap.Width)x$($iconBitmap.Height) $($iconBitmap.PixelFormat)."
    }
    foreach ($corner in @(@(0, 0), @(($iconBitmap.Width - 1), 0),
        @(0, ($iconBitmap.Height - 1)), @(($iconBitmap.Width - 1), ($iconBitmap.Height - 1)))) {
        if ($iconBitmap.GetPixel($corner[0], $corner[1]).A -gt 2) {
            throw "Icon atlas corner is not transparent: $($corner[0]),$($corner[1])."
        }
    }
    $cell = [int]($iconBitmap.Width / 3)
    for ($row = 0; $row -lt 3; $row++) {
        for ($column = 0; $column -lt 3; $column++) {
            $opaqueSamples = 0
            for ($y = $row * $cell; $y -lt ($row + 1) * $cell; $y += 8) {
                for ($x = $column * $cell; $x -lt ($column + 1) * $cell; $x += 8) {
                    if ($iconBitmap.GetPixel($x, $y).A -gt 32) { $opaqueSamples++ }
                }
            }
            if ($opaqueSamples -lt 40) {
                throw "Icon atlas cell $row,$column does not contain a substantive alpha glyph."
            }
        }
    }
}
finally {
    $iconBitmap.Dispose()
}
$placementStart = $painter.IndexOf('bool editingPlacement')
$placementEnd = $painter.IndexOf('GUILayout.Label("Hover previews one layer', $placementStart)
if ($placementStart -lt 0 -or $placementEnd -le $placementStart) { throw 'Placement toolbar block was not found.' }
$placement = $painter.Substring($placementStart, $placementEnd - $placementStart)
$resetAt = $placement.IndexOf('DrawIconButton(ResetIconIndex')
$moveAt = $placement.IndexOf('DrawIconToggle(moving, MoveIconIndex')
$rotateAt = $placement.IndexOf('DrawIconToggle(rotating, RotateIconIndex')
if ($resetAt -lt 0 -or $moveAt -le $resetAt -or $rotateAt -le $moveAt) {
    throw 'Placement controls are not ordered Reset, Move, Rotate.'
}

foreach ($thermalContract in @('ThermalMassFraction = 0.05f',
    'InternalTemperatureMultiplier = 1.35', 'SkinTemperatureMultiplier = 1.50',
    'InternalTemperatureCap = 3000.0', 'SkinTemperatureCap = 3300.0')) {
    if (-not $module.Contains($thermalContract)) { throw "Missing thermal contract: $thermalContract" }
}

Get-ChildItem -LiteralPath $managed -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {}
}
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$moduleType = $assembly.GetType('HeatTileOverlayPrototype.ModuleHeatTileOverlay', $true)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$compiledConstants = @{
    ThermalMassFraction = [single]0.05
    InternalTemperatureMultiplier = [double]1.35
    SkinTemperatureMultiplier = [double]1.50
    InternalTemperatureCap = [double]3000
    SkinTemperatureCap = [double]3300
}
foreach ($entry in $compiledConstants.GetEnumerator()) {
    $actual = [double]$moduleType.GetField($entry.Key, $flags).GetRawConstantValue()
    if ([Math]::Abs($actual - [double]$entry.Value) -gt 0.0001) {
        throw "Compiled constant $($entry.Key) is $actual, expected $($entry.Value)."
    }
}

Write-Output 'UI refinement, icon mapping, defaults, mirror, and thermal-balance contracts passed.'
