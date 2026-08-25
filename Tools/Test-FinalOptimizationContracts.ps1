$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$painterPath = Join-Path $projectRoot 'Source\HeatTilePainter.cs'
$modulePath = Join-Path $projectRoot 'Source\ModuleHeatTileOverlay.cs'
$bridgePath = Join-Path $projectRoot 'Source\ReforgedPaintBridge.cs'
$painter = Get-Content -Raw -LiteralPath $painterPath
$module = Get-Content -Raw -LiteralPath $modulePath
$bridge = Get-Content -Raw -LiteralPath $bridgePath

foreach ($required in @(
    'GameDatabase.Instance.GetTexture(EditorIconAtlasPath, false)'
    'Time.unscaledTime >= _nextStockIconResolveTime'
    '_nextStockIconResolveTime = Time.unscaledTime + 1f'
    'EditorLogic.fetch'
    'editor.toolsUI'
    'BindingFlags.Instance | BindingFlags.NonPublic'
    'DataUtility.GetOuterUV(image.sprite)'
    'GUI.DrawTextureWithTexCoords(iconRect, icon.Texture, icon.Uv, true)'
    'GameDatabase and EditorToolsUI own these textures. Release references only.'
)) {
    if (-not $painter.Contains($required)) { throw "Missing bounded icon/runtime contract: $required" }
}

foreach ($forbidden in @(
    'CreateEditorIcon'
    'new Texture2D'
    'GetPixels('
    'GetPixels32('
    'SetPixels('
    'SetPixels32('
    'ReadPixels('
    'Graphics.Blit('
    'Graphics.CopyTexture('
)) {
    if ($painter.Contains($forbidden)) { throw "Per-instance/per-frame icon texture work remains: $forbidden" }
}

$partModuleSource = $module.Substring(0, $module.IndexOf('public sealed class HeatTileOverlayScheduler'))
if ($partModuleSource -match 'public void Update\(' -or
    $partModuleSource -match 'public void LateUpdate\(') {
    throw 'Final optimization introduced a per-part frame callback.'
}
if ($module -notmatch 'one part per frame' -or
    $module -notmatch 'if \(!PumpInitialRebuildQueue\(\)\) PumpScheduledRebuildQueue\(\);') {
    throw 'Saved-craft restoration/repaint is no longer globally bounded.'
}
if ($bridge -notmatch 'ApplyExternalZones\(string\.Join' -or
    $painter -match 'RebuildOverlay\(') {
    throw 'The UI refinement bypassed the atomic paint bridge or added a UI-driven rebuild path.'
}

Write-Output 'Final icon ownership, allocation, scheduling, and repaint-boundary audit passed.'
