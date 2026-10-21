$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$modulePath = Join-Path $projectRoot 'Source\ModuleHeatTileOverlay.cs'
$bridgePath = Join-Path $projectRoot 'Source\ReforgedPaintBridge.cs'
$corePath = Join-Path $projectRoot 'Source\ReforgedBackend.cs'
$painterPath = Join-Path $projectRoot 'Source\HeatTilePainter.cs'
$assemblyPath = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'
. (Join-Path $projectRoot 'Tools\Resolve-KspManaged.ps1')
$managed = Get-KspManagedPath

$moduleSource = Get-Content -Raw -LiteralPath $modulePath
$bridgeSource = Get-Content -Raw -LiteralPath $bridgePath
$coreSource = Get-Content -Raw -LiteralPath $corePath
$painterSource = Get-Content -Raw -LiteralPath $painterPath

if ($moduleSource -notmatch 'public float PlaneOffset;' -or
    $moduleSource -notmatch 'values\.Length >= 19 \? ParseFloat\(values\[18\], 0f\)' -or
    $moduleSource -notmatch '100f - state\.Coverage \+ state\.PlaneOffset') {
    throw 'Plane placement is not appended backward-compatibly to authoritative layerData.'
}
if ($bridgeSource -notmatch '1000 \+ Mathf\.Clamp\(Mathf\.RoundToInt\(EffectiveCutPercent\(layer\) \* 10f\)') {
    throw 'The bridge is not emitting precise versioned coverage cuts.'
}
if ($coreSource -notmatch 'c >= 1000[\s\S]*?\(c - 1000\) / 1000f' -or
    $coreSource -match 'const double K = 2\.5') {
    throw 'The core does not decode precise cuts or still owns the unbalanced upstream heat buff.'
}
if ($painterSource -notmatch 'GizmoOffset\.Attach' -or
    $painterSource -notmatch '\.useGrid' -or $painterSource -notmatch '\.SnapInterval' -or
    $painterSource -match 'DrawDirectionRotationRow|RotateFineDirection') {
    throw 'The stock move gizmo or simplified placement UI contract is missing.'
}
if ($painterSource -notmatch 'EditorIconAtlasPath' -or $painterSource -notmatch 'ToolTips' -or
    $painterSource -notmatch 'DrawIconToggle\(moving, MoveIconIndex' -or
    $painterSource -notmatch 'TryAssignStockIcon\(editor\.toolsUI, "moveButton"') {
    throw 'The stock-sprite/generated-atlas icon header or placement controls are missing.'
}
if ($moduleSource -notmatch 'IPartMassModifier' -or
    $moduleSource -notmatch 'ThermalMassFraction = 0\.05f' -or
    $moduleSource -notmatch 'InternalTemperatureMultiplier = 1\.35' -or
    $moduleSource -notmatch 'SkinTemperatureMultiplier = 1\.50' -or
    $moduleSource -notmatch 'InternalTemperatureCap = 3000\.0' -or
    $moduleSource -notmatch 'SkinTemperatureCap = 3300\.0') {
    throw 'Balanced thermal protection constants or KSP mass integration are missing.'
}
$partModuleSource = $moduleSource.Substring(0, $moduleSource.IndexOf('public sealed class HeatTileOverlayScheduler'))
if ($partModuleSource -match 'public void Update\(' -or $partModuleSource -match 'public void LateUpdate\(') {
    throw 'Precision/thermal features introduced a per-part frame callback.'
}

Get-ChildItem -LiteralPath $managed -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {}
}
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$moduleType = $assembly.GetType('HeatTileOverlayPrototype.ModuleHeatTileOverlay', $true)
$massInterface = [IPartMassModifier]
if (-not $massInterface.IsAssignableFrom($moduleType)) {
    throw 'The compiled part module does not implement IPartMassModifier.'
}
foreach ($method in @('GetModuleMass', 'GetModuleMassChangeWhen', 'SetThermalProtection',
    'TryGetCoveragePlane', 'EffectiveCutPercent')) {
    if ($null -eq $moduleType.GetMethod($method)) { throw "Compiled feature method missing: $method" }
}
$layerType = $assembly.GetType('HeatTileOverlayPrototype.HeatTileLayerState', $true)
$layer = [Activator]::CreateInstance($layerType)
$layerType.GetField('Coverage').SetValue($layer, [single]35)
$layerType.GetField('PlaneOffset').SetValue($layer, [single]2.5)
$layerType.GetField('PatternScale').SetValue($layer, [single]1.75)
$serializer = $moduleType.GetMethod('SerializeLayer',
    [Reflection.BindingFlags]'NonPublic,Static')
$record = [string]$serializer.Invoke($null, @($layer))
if ($record.Split('|').Length -ne 20 -or $record.Split('|')[-2] -ne '2.5' -or
    $record.Split('|')[-1] -ne '1.75') {
    throw "Compiled layer serialization is not the expected backward-compatible 20-field append: $record"
}
$effectiveCut = [single]$moduleType.GetMethod('EffectiveCutPercent').Invoke($null, @($layer))
if ([Math]::Abs($effectiveCut - 67.5) -gt 0.0001) {
    throw "Compiled coverage + placement calculation is incorrect: $effectiveCut"
}

$coreType = $assembly.GetType('HeatTileOverlayPrototype.ReforgedCore.WeatherCache', $true)
$parser = $coreType.GetMethod('RegionAxisDir',
    [Reflection.BindingFlags]'NonPublic,Public,Static')
$args = @('v0_-1000_0_1655_0_100_1_82', $null, [single]0, [single]0, [single]0)
if (-not $parser.Invoke($null, $args)) { throw 'Precise vector token was rejected.' }
if ([Math]::Abs([double]$args[2] - 0.655) -gt 0.0001) {
    throw "Precise cut decoded incorrectly: $($args[2])"
}

Write-Output 'Precision placement, icon UI, and balanced thermal contracts passed.'
