$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$libraryPath = Join-Path $projectRoot 'Source\HeatTileTextureLibrary.cs'
$modulePath = Join-Path $projectRoot 'Source\ModuleHeatTileOverlay.cs'
$bridgePath = Join-Path $projectRoot 'Source\ReforgedPaintBridge.cs'
$backendPath = Join-Path $projectRoot 'Source\ReforgedBackend.cs'
$painterPath = Join-Path $projectRoot 'Source\HeatTilePainter.cs'
$assemblyPath = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'
. (Join-Path $projectRoot 'Tools\Resolve-KspManaged.ps1')
$managed = Get-KspManagedPath

$library = Get-Content -Raw -LiteralPath $libraryPath
$module = Get-Content -Raw -LiteralPath $modulePath
$bridge = Get-Content -Raw -LiteralPath $bridgePath
$backend = Get-Content -Raw -LiteralPath $backendPath
$painter = Get-Content -Raw -LiteralPath $painterPath

foreach ($contract in @(
    'GameDatabase.Instance.GetConfigNodes("HEAT_TILE_TEXTURE")'
    'AddOrReplaceDefinition(definition, true)'
    'TryResolveTexture(string id, bool normalMap'
    'PatternSpan = ParsePositiveFloat'
    'PluginData/UserTextures'
    'ContentHash(bytes)'
    'root.Save(temporary)'
)) {
    if (-not $library.Contains($contract)) { throw "Missing texture-library contract: $contract" }
}
if ($library -match 'AddValue\("(?:source|sourcePath|absolutePath)"') {
    throw 'The imported-texture manifest persists a source PC path.'
}
foreach ($contract in @(
    'public float PatternScale = 1f;'
    'values.Length >= 20 ? ParseFloat(values[19], 1f) : 1f'
    'state.PatternScale = Mathf.Clamp(FiniteOr(state.PatternScale, 1), 0.25f, 4f);'
)) {
    if (-not $module.Contains($contract)) { throw "Missing pattern persistence contract: $contract" }
}
if (-not $bridge.Contains(': definition.Id;') -or
    -not $bridge.Contains('Mathf.Clamp(layer.PatternScale, 0.25f, 4f)')) {
    throw 'The active bridge does not carry definition identity and per-layer scale.'
}
foreach ($contract in @(
    'name.EndsWith("_b", StringComparison.OrdinalIgnoreCase)'
    'FindExact(definitionName, out definition)'
    'return Mathf.Max(0.05f, definition.PatternSpan);'
    'UvWorldScale(Part part, Texture sourceTexture)'
    'MaterialUsesTexture(material, source)'
    'worldPerU.magnitude'
    'worldPerV.magnitude'
    'HasWorldScaledLayer(stackSpec)'
    '"|u" + GeometrySignature(part) + "_" + MaterialUvSignature'
    'internal sealed class BakeProjection'
    'ApplyProjectedLayer(projection'
    'ProjectedCoordinate(projection, position, axis, spanU, spanV)'
    'ForRegion(BakeProjection source, string region)'
    'key += "|p" + projection.Signature'
    'SoupProjection(r, V, N,'
    'painted, pris[0])'
    'uvScaleCache.Clear();'
)) {
    if (-not $backend.Contains($contract)) { throw "Missing material-scale contract: $contract" }
}
foreach ($contract in @(
    'Browse / Add custom...'
    'ProcessPendingTextureBrowserNavigation();'
    'ProcessPendingTextureImport();'
    'DrawPatternScaleControls()'
    'HeatTileTextureLibrary.DisplayName(layer.TextureId)'
    '_paintLayoutSnapshotValid'
    'Event.current.type == EventType.Layout'
    'if (_paintLayoutFlatColor)'
    'if (_paintLayoutMirrorMode != 0)'
    'if (_paintLayoutDirectionFacing)'
    'if (_paintLayoutEdgeStyle != 0)'
)) {
    if (-not $painter.Contains($contract)) { throw "Missing texture browser/UI contract: $contract" }
}

Get-ChildItem -LiteralPath $managed -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {}
}
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$layerType = $assembly.GetType('HeatTileOverlayPrototype.HeatTileLayerState', $true)
$patternField = $layerType.GetField('PatternScale')
if ($null -eq $patternField -or [Math]::Abs([single]$patternField.GetValue([Activator]::CreateInstance($layerType)) - 1) -gt 0.0001) {
    throw 'Compiled pre-update layer default is not 1x.'
}

$cacheType = $assembly.GetType('HeatTileOverlayPrototype.ReforgedCore.WeatherCache', $true)
$histogramType = $cacheType.GetNestedType('UvScaleHistogram', [Reflection.BindingFlags]'NonPublic')
if ($null -eq $histogramType) { throw 'Compiled bounded UV-scale median is missing.' }
$histogram = [Activator]::CreateInstance($histogramType, $true)
$add = $histogramType.GetMethod('Add')
$median = $histogramType.GetMethod('Median')
$add.Invoke($histogram, @([double]2, [double]100)) | Out-Null
$add.Invoke($histogram, @([double]1000, [double]1)) | Out-Null
$actual = [single]$median.Invoke($histogram, @())
if ($actual -lt 1.7 -or $actual -gt 2.4) {
    throw "Weighted median was skewed by a tiny UV outlier: $actual"
}
$projectionType = $cacheType.GetNestedType('BakeProjection', [Reflection.BindingFlags]'NonPublic')
if ($null -eq $projectionType) { throw 'Compiled part-local bake projection is missing.' }
$projection = [Activator]::CreateInstance($projectionType, $true)
$forRegion = $cacheType.GetMethod('ForRegion', [Reflection.BindingFlags]'Static, NonPublic')
if ($null -eq $forRegion) { throw 'Compiled coverage-direction projection mapping is missing.' }
$positive = $forRegion.Invoke($null, @($projection, 'v0_0_1000_1500_0_100_1_82'))
$negative = $forRegion.Invoke($null, @($projection, 'v0_0_-1000_1500_0_100_1_82'))
$basisU = $projectionType.GetField('BasisU', [Reflection.BindingFlags]'Instance, NonPublic')
$basisV = $projectionType.GetField('BasisV', [Reflection.BindingFlags]'Instance, NonPublic')
$pu = $basisU.GetValue($positive); $nu = $basisU.GetValue($negative)
$pv = $basisV.GetValue($positive); $nv = $basisV.GetValue($negative)
if (($pu - $nu).sqrMagnitude -gt 0.000001 -or ($pv - $nv).sqrMagnitude -gt 0.000001) {
    throw 'Opposite coverage directions do not share one projection plane and phase.'
}

Write-Output 'Texture definition, custom import, persistence, stable GUI, and part-local projection contracts passed.'
