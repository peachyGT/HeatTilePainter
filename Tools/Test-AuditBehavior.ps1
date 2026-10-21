$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Resolve-KspManaged.ps1')
Get-ChildItem -LiteralPath (Get-KspManagedPath) -Filter '*.dll' | ForEach-Object { try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {} }
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'))
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$cache=$assembly.GetType('HeatTileOverlayPrototype.ReforgedCore.WeatherCache',$true)
$decode=$cache.GetMethod('StackStrength',$flags)
for($percent=0;$percent -le 100;$percent++) {
    $actual=[single]$decode.Invoke($null,@([int](1000+$percent)))
    if([Math]::Abs($actual-$percent/100.0) -gt .00001) { throw "Encoded opacity $percent failed: $actual" }
}
foreach($pair in @(@(1,.6),@(2,2.5),@(5,2.5),@(50,.5))) {
    if([Math]::Abs([single]$decode.Invoke($null,@([int]$pair[0]))-$pair[1]) -gt .00001) { throw 'Legacy strength compatibility failed' }
}
$parse=$assembly.GetType('HeatTileOverlayPrototype.ModuleHeatTileOverlay',$true).GetMethod('ParseFloat',$flags)
foreach($value in @('NaN','Infinity','-Infinity','not-a-number')) {
    if([single]$parse.Invoke($null,@($value,[single]7)) -ne 7) { throw "Non-finite persistence accepted: $value" }
}
$number=[single]$parse.Invoke($null,@('1.75',[single]7))
if($number -ne 1.75) { throw 'Valid invariant persistence rejected' }
$maskHash=$cache.GetMethod('MaskHash',$flags)
$maskA=[single[]]::new(32)
$maskB=[single[]]::new(32)
$maskB[1]=.0001
$a=$maskHash.Invoke($null,@(,$maskA))
$b=$maskHash.Invoke($null,@(,$maskB))
if($a -eq $b) { throw 'Mask cache aliases an unsampled, sub-byte pixel change' }
$zero=[single[]]::new(16384)
$one=[single[]]::new(16384)
for($i=0;$i -lt $one.Length;$i++) { $one[$i]=1 }
if($maskHash.Invoke($null,@(,$zero)) -eq $maskHash.Invoke($null,@(,$one))) {
    throw 'Mask cache aliases uniform transparent and opaque masks'
}
Write-Output 'Audit behavior passed: 101 opacity percentages, legacy tokens, malformed numeric persistence, and complete mask identity (automated, not visual verification).'
