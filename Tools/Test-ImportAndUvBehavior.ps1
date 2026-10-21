$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Resolve-KspManaged.ps1')
Get-ChildItem -LiteralPath (Get-KspManagedPath) -Filter '*.dll' | ForEach-Object { try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {} }
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'))
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$image=$assembly.GetType('HeatTileOverlayPrototype.ImageImportPolicy',$true).GetMethod('HasBoundedDimensions',$flags)
function Test-Dimensions([byte[]]$Bytes,[bool]$Expected,[string]$Label) {
    if([bool]$image.Invoke($null,@($Bytes,4096)) -ne $Expected) {throw "Image header case failed: $Label"}
}
$png=[byte[]]@(137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,16,0,0,0,16,0)
Test-Dimensions $png $true '4096 square PNG'
$png[19]=1
Test-Dimensions $png $false '4097 wide PNG before decode'
$png[16]=128
Test-Dimensions $png $false 'overflowed PNG dimension'
Test-Dimensions ([byte[]]@(1,2,3)) $false 'truncated header'
$jpg=[byte[]]@(255,216,255,224,0,4,0,0,255,192,0,17,8,4,0,8,0,3,1,17,0,2,17,0,3,17,0)
Test-Dimensions $jpg $true 'JPEG SOF after APP'
$jpg[14]=1;$jpg[13]=16
Test-Dimensions $jpg $false 'oversized JPEG'
$jpg[13]=4;$jpg[14]=0;$jpg[9]=194
Test-Dimensions $jpg $true 'progressive JPEG'
$jpg[10]=255
Test-Dimensions $jpg $false 'truncated JPEG segment'
$partition=$assembly.GetType('HeatTileOverlayPrototype.UvPaintPartition',$true).GetMethod('Split',$flags)
$uv=New-Object 'System.Collections.Generic.List[UnityEngine.Vector2]'
foreach($p in @(@(0,0),@(1,0),@(0,1),@(1,1))) {$uv.Add((New-Object UnityEngine.Vector2 ([single]$p[0]),([single]$p[1])))}
$tris=New-Object 'System.Collections.Generic.List[int]'
foreach($index in @(0,1,2,1,3,2)){$tris.Add($index)}
$result=$partition.Invoke($null,@($uv.psobject.BaseObject,$tris.psobject.BaseObject,[UnityEngine.Vector2]::one,[UnityEngine.Vector2]::zero))
if($result.Count -ne 1) {throw 'Shared edges must not create extra batches'}
foreach($index in @(0,1,2)){$tris.Add($index)}
$result=$partition.Invoke($null,@($uv.psobject.BaseObject,$tris.psobject.BaseObject,[UnityEngine.Vector2]::one,[UnityEngine.Vector2]::zero))
if($result.Count -ne 2 -or ($result | ForEach-Object Count | Measure-Object -Sum).Sum -ne 9) {throw 'Overlapping faces must be separated without losing indices'}
$uv[0]=New-Object UnityEngine.Vector2 -1,0
$result=$partition.Invoke($null,@($uv.psobject.BaseObject,$tris.psobject.BaseObject,[UnityEngine.Vector2]::one,[UnityEngine.Vector2]::zero))
if($result.Count -ne 1) {throw 'Repeating atlas fallback must remain bounded'}
Write-Output 'Image-header bounds and UV overlap behavior passed (automated geometry checks, not KSP visual verification).'
