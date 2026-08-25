$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $projectRoot 'Tools\Resolve-KspManaged.ps1')
$managed = Get-KspManagedPath
$pluginPath = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Plugins\HeatTileOverlayPrototype.dll'

Get-ChildItem -LiteralPath $managed -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {}
}

$plugin = [Reflection.Assembly]::LoadFrom($pluginPath)
$assemblyCSharp = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))
$schedulerType = $plugin.GetType('HeatTileOverlayPrototype.HeatTileOverlayScheduler', $true)
$target = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($schedulerType)
$sceneType = $assemblyCSharp.GetType('GameScenes', $true)
$eventOpenType = $assemblyCSharp.GetType('EventData`1', $true)
$eventType = $eventOpenType.MakeGenericType([Type[]]@($sceneType))
$channel = [Activator]::CreateInstance($eventType, [object[]]@('HeatTilePainterAudit'))
$delegateType = $eventType.GetMethod('Add').GetParameters()[0].ParameterType
$handlerMethod = $schedulerType.GetMethod(
    'OnSceneLoadRequested', [Reflection.BindingFlags]'NonPublic,Instance')
$handler = [Delegate]::CreateDelegate($delegateType, $target, $handlerMethod)

$eventType.GetMethod('Add').Invoke($channel, [object[]]@($handler)) | Out-Null
$eventType.GetMethod('Remove').Invoke($channel, [object[]]@($handler)) | Out-Null

if ($null -eq $handler.Target) {
    throw 'Scheduler callback unexpectedly has a null target.'
}
Write-Output "KSP EventData instance callback audit passed: $($handler.Target.GetType().FullName)"
