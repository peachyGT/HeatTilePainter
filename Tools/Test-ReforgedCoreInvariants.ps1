$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $projectRoot 'Tools\Resolve-KspManaged.ps1')
$managed = Get-KspManagedPath
Get-ChildItem -LiteralPath $managed -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {}
}

$assembly = [Reflection.Assembly]::LoadFrom(
    (Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'))
$moduleType = $assembly.GetType('HeatTileOverlayPrototype.ModuleHeatTileOverlay', $true)
$cacheType = $assembly.GetType('HeatTileOverlayPrototype.ReforgedCore.WeatherCache', $true)
$regionMethod = $cacheType.GetMethod('RegionAxisDir',
    [Reflection.BindingFlags]'NonPublic,Static')
$facingMethod = $cacheType.GetMethod('RegionFacingWeight',
    [Reflection.BindingFlags]'NonPublic,Static')
$vectorMethod = $moduleType.GetMethod('VectorRegion',
    [Reflection.BindingFlags]'NonPublic,Static')

if ($null -ne $moduleType.GetNestedType('FlatRegionVertex',
    [Reflection.BindingFlags]'NonPublic')) {
    throw 'The retired flat-region implementation is still compiled.'
}
if ($null -ne $moduleType.GetMethod('TryRebuildBakedPaint',
    [Reflection.BindingFlags]'NonPublic,Instance')) {
    throw 'The retired bake implementation is still compiled.'
}

function Test-Region([string]$token, [single[]]$expectedDirection,
    [single]$expectedCut, [single]$expectedFade) {
    [object[]]$arguments = @($token, [UnityEngine.Vector3]::zero,
        [single]0, [single]0, [single]0)
    $ok = [bool]$regionMethod.Invoke($null, $arguments)
    if (-not $ok) { throw "Region token was rejected: $token" }
    $direction = [UnityEngine.Vector3]$arguments[1]
    if ([Math]::Abs($direction.x - $expectedDirection[0]) -gt 0.002 -or
        [Math]::Abs($direction.y - $expectedDirection[1]) -gt 0.002 -or
        [Math]::Abs($direction.z - $expectedDirection[2]) -gt 0.002 -or
        [Math]::Abs([single]$arguments[2] - $expectedCut) -gt 0.002 -or
        [Math]::Abs([single]$arguments[3] - $expectedFade) -gt 0.002) {
        throw "Region token decoded incorrectly: $token"
    }
    Write-Output "Decoded $token => dir=$direction cut=$($arguments[2]) fade=$($arguments[3])"
}

Test-Region 'v0_-1000_0_65_5_100_1_82' @(0,-1,0) 0.65 0.05
Test-Region 'v707_0_707_25_1_100_0_82' @(0.707,0,0.707) 0.25 0.01

[object[]]$encode = @([UnityEngine.Vector3]::down, 65, 5, $true, [single]82)
$encoded = [string]$vectorMethod.Invoke($null, $encode)
if ($encoded -ne 'v0_-1000_0_65_5_100_1_82') {
    throw "Direction encoder produced '$encoded'."
}

$toward = [single]$facingMethod.Invoke($null,
    @('v0_-1000_0_65_5_100_1_82', [UnityEngine.Vector3]::down))
$away = [single]$facingMethod.Invoke($null,
    @('v0_-1000_0_65_5_100_1_82', [UnityEngine.Vector3]::up))
$disabled = [single]$facingMethod.Invoke($null,
    @('v0_-1000_0_65_5_100_0_82', [UnityEngine.Vector3]::up))
if ($toward -lt 0.99 -or $away -gt 0.01 -or $disabled -lt 0.99) {
    throw "Direction-facing mask weights are $toward/$away/$disabled."
}

Write-Output 'Reforged core vector/retired-backend invariants passed.'
