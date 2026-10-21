$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$modulePath = Join-Path $projectRoot 'Source\ModuleHeatTileOverlay.cs'
$bridgePath = Join-Path $projectRoot 'Source\ReforgedPaintBridge.cs'
$corePath = Join-Path $projectRoot 'Source\ReforgedBackend.cs'
$painterPath = Join-Path $projectRoot 'Source\HeatTilePainter.cs'
$buildPath = Join-Path $projectRoot 'build.ps1'
$assemblyPath = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'
. (Join-Path $projectRoot 'Tools\Resolve-KspManaged.ps1')
$managed = Get-KspManagedPath

foreach ($path in @($modulePath, $bridgePath, $corePath, $painterPath, $buildPath, $assemblyPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required audit input is missing: $path" }
}

$moduleSource = Get-Content -Raw -LiteralPath $modulePath
$bridgeSource = Get-Content -Raw -LiteralPath $bridgePath
$coreSource = Get-Content -Raw -LiteralPath $corePath
$painterSource = Get-Content -Raw -LiteralPath $painterPath
$buildSource = Get-Content -Raw -LiteralPath $buildPath

if ($painterSource -notmatch 'Heat Tile Painter 0\.25\.0') {
    throw 'The editor window does not identify the 0.25.0 build.'
}
if ($buildSource -match 'BakedPaintRenderer\.cs|FlatRegionPaintRenderer\.cs') {
    throw 'A retired persistent renderer is still present in the compiler source list.'
}

if ($moduleSource -match 'public void LateUpdate\(') {
    throw 'ModuleHeatTileOverlay still pays a Unity LateUpdate cost on every patched part.'
}
if ($moduleSource -notmatch 'class HeatTileOverlayScheduler' -or
    $moduleSource -notmatch 'PumpRebuildQueues\(\)' -or
    $moduleSource -notmatch 'one part per frame') {
    throw 'The single global, bounded repaint scheduler is missing.'
}
$moduleStart = [regex]::Match($moduleSource,
    'public override void OnStart\(StartState state\)[\s\S]*?public void OnDestroy').Value
if ($moduleSource -match '_eventsRegistered' -or
    $moduleStart -match 'GameEvents\.onEditorShipModified\.Add') {
    throw 'Editor events are still subscribed once per patched part.'
}
if ($moduleSource -match 'KSPAddon\(KSPAddon\.Startup\.Instantly[\s\S]*?class HeatTileOverlayScheduler' -or
    $moduleStart -notmatch 'HeatTileOverlayScheduler\.EnsureRunning\(\)') {
    throw 'The repaint scheduler still depends on the unsafe Instantly startup phase.'
}
if ($moduleSource -notmatch 'GameEvents\.onGameSceneLoadRequested != null' -or
    $moduleSource -notmatch 'Global repaint scheduler online') {
    throw 'The repaint scheduler does not safely defer GameEvents registration.'
}
if ($moduleSource -match 'private static void OnSceneLoadRequested' -or
    $moduleSource -match 'private static void OnEditorShipModified' -or
    $moduleSource -match 'private static void OnVariantApplied') {
    throw 'KSP EventData callbacks cannot be static because its delegate wrapper dereferences Target.'
}
if ($painterSource -notmatch 'GizmoRotate\.Attach' -or
    $painterSource -notmatch 'SetCoordSystem\(Space\.Self\)' -or
    $painterSource -notmatch '\.useAngleSnap' -or $painterSource -notmatch '\.SnapDegrees') {
    throw 'The direction editor is not using KSP''s stock snap-aware rotate gizmo.'
}
if ($painterSource -match 'GetKeyDown\(KeyCode\.C\)') {
    throw 'The painter is shadowing KSP/Editor Extensions snap input instead of using stock state.'
}
$rebuild = [regex]::Match($moduleSource,
    'private void RebuildOverlay\(\)[\s\S]*?private void BuildLayerOverlay').Value
if ($rebuild -notmatch 'TryRebuildReforgedPaint\(\)' -or
    $rebuild -match 'TryRebuildBakedPaint|TryRebuildFlatRegionPaint|DestroyOverlay\(\)') {
    throw 'RebuildOverlay is not exclusively routed through the Reforged bridge.'
}
$destroy = [regex]::Match($moduleSource,
    'private void DestroyOverlay\(\)[\s\S]*?\n\s*}\n\s*}\n\s*}').Value
if ($destroy -notmatch 'ClearReforgedPaint\(\)' -or $destroy -match 'RestoreBakedPaint') {
    throw 'DestroyOverlay is not exclusively routed through the Reforged bridge.'
}
if ($bridgeSource -notmatch 'ApplyExternalZones\(string\.Join' -or
    $bridgeSource -notmatch 'ModuleHeatTileReforgedCore') {
    throw 'The layer-to-Reforged atomic bridge is missing.'
}
if ($bridgeSource -notmatch 'float fadeOfPaintedDepth[\s\S]*?\? 0f' -or
    $bridgeSource -notmatch 'Mathf\.Clamp\(Mathf\.RoundToInt\(fadeOfPaintedDepth\), 0, 40\)') {
    throw 'Hard regions no longer request Reforged''s exact geometric boundary.'
}
if ($coreSource -notmatch 'bool allFaded = false;') {
    throw 'Placed patterned regions can leak through overlapping top/bottom UV islands.'
}

$coreHeader = $coreSource.Substring(0, $coreSource.IndexOf('public bool Active'))
if ($coreHeader -match 'KSPField\(isPersistant\s*=\s*true\)') {
    throw 'The derived Reforged core state is being persisted independently of layerData.'
}
$restore = [regex]::Match($coreSource,
    'void RestoreOriginals\(\)[\s\S]*?void ReapplyVariant').Value
if ($restore -notmatch 'sharedMaterials\s*=\s*kv\.Value[\s\S]*?RestoreMeshesToOriginal\(\)') {
    throw 'Pristine restore no longer restores materials before meshes.'
}
if ($coreSource -notmatch 'public void ClearAll\(\)[\s\S]*?ClearAll\(false\)' -or
    $coreSource -notmatch 'public void ClearAll\(bool reapplyVariant\)[\s\S]*?RestoreOriginals\(reapplyVariant\)') {
    throw 'Ordinary erase/remove-all can still cycle a stock part variant.'
}
if ($moduleSource -notmatch 'ClearReforgedPaint\(true\)' -or
    $bridgeSource -notmatch 'core\.ClearAll\(reapplyVariant\)') {
    throw 'Variant reapplication is not isolated to the explicit variant-change path.'
}
if ($coreSource -notmatch 'Material\[\] live = r\.sharedMaterials;[\s\S]*?Material\[\] cur = live;') {
    throw 'Pristine capture can still treat Unity-instantiated materials as originals.'
}
if ($coreSource -notmatch 'ApplyPaintSurfaceProperties\(m, z\)' -or
    $coreSource -notmatch '"_MetallicGlossMap"' -or
    $coreSource -notmatch 'material\.SetTexture\(mapProperties\[i\], Texture2D\.blackTexture\)') {
    throw 'Flat region paint can still inherit source gloss/metal maps.'
}
if ($coreSource -notmatch 'if \(UsesDynamicMeshSource\(\)\) return null;') {
    throw 'Procedural parts can still be replaced by their prefab seed mesh.'
}
if ($coreSource -notmatch 'Multi-material renderers cannot use the upstream one-submesh partitioner') {
    throw 'Complex renderers no longer have a topology-preserving material-mask path.'
}
if ($coreSource -notmatch '#if HEATTILE_REFORGED_UPSTREAM_UI[\s\S]*?#endif[\s\S]*?internal static class WeatherCache') {
    throw 'The upstream Reforged painter UI is not compiled out.'
}
if ($coreSource -notmatch 'const string OverlayPath = "HeatTileOverlayPrototype/Assets/";') {
    throw 'The Reforged core is not using Heat Tile Painter assets.'
}

Get-ChildItem -LiteralPath $managed -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {}
}
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$moduleType = $assembly.GetType('HeatTileOverlayPrototype.ModuleHeatTileOverlay', $true)
$coreType = $assembly.GetType(
    'HeatTileOverlayPrototype.ReforgedCore.ModuleHeatTileReforgedCore', $true)
if ($null -ne $moduleType.GetMethod('TryRebuildBakedPaint',
    [Reflection.BindingFlags]'NonPublic,Instance') -or
    $null -ne $moduleType.GetNestedType('FlatRegionVertex',
    [Reflection.BindingFlags]'NonPublic')) {
    throw 'A retired bespoke paint backend was compiled into the DLL.'
}
if ($null -eq $coreType.GetMethod('ApplyExternalZones')) {
    throw 'Compiled Reforged core does not expose atomic zone application.'
}
if ($null -ne $assembly.GetType('ReforgedRedux.ReforgedPainterEditor', $false) -or
    $null -ne $assembly.GetType('ReforgedRedux.ReforgedPainterFlight', $false)) {
    throw 'The upstream Reforged UI leaked into the compiled assembly.'
}
if ($null -ne $assembly.GetType('HeatTileOverlayPrototype.HeatTileOverlayCameraBias', $false)) {
    throw 'The per-camera overlay follower was compiled into the assembly.'
}
if ($null -ne $moduleType.GetMethod('LateUpdate',
    [Reflection.BindingFlags]'Public,NonPublic,Instance,DeclaredOnly')) {
    throw 'The compiled part module still contains a per-part LateUpdate.'
}
if ($null -eq $assembly.GetType('HeatTileOverlayPrototype.HeatTileOverlayScheduler', $false)) {
    throw 'The compiled assembly is missing the global repaint scheduler.'
}

$version = $assembly.GetName().Version.ToString()
if ($version -ne '0.25.0.0') { throw "Unexpected assembly version: $version" }

Write-Output "Backend safety audit passed for Heat Tile Painter $version."
Write-Output 'Persistent path: layerData -> atomic Reforged zones -> pristine restore.'
Write-Output 'Retired bake monitor/UI/camera follower: unreachable or absent.'
