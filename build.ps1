$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $projectRoot 'Tools\Resolve-KspManaged.ps1')
$managed = Get-KspManagedPath
$runtimeDirectory = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
$compiler = Join-Path $runtimeDirectory 'csc.exe'
$outputDir = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype\Plugins'
$output = Join-Path $outputDir 'HeatTileOverlayPrototype.dll'

if (-not (Test-Path -LiteralPath $compiler)) { throw "C# compiler not found: $compiler" }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$arguments = @(
    '/nologo'
    '/target:library'
    '/optimize+'
    '/platform:anycpu'
    "/out:$output"
    "/reference:$(Join-Path $managed 'Assembly-CSharp.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.AnimationModule.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.CoreModule.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.IMGUIModule.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.ImageConversionModule.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.InputLegacyModule.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.PhysicsModule.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.UI.dll')"
    "/reference:$(Join-Path $managed 'UnityEngine.UIModule.dll')"
    (Join-Path $projectRoot 'Source\AssemblyInfo.cs')
    (Join-Path $projectRoot 'Source\ThermalProtectionPolicy.cs')
    (Join-Path $projectRoot 'Source\ExteriorPaintPolicy.cs')
    (Join-Path $projectRoot 'Source\UvPaintPartition.cs')
    (Join-Path $projectRoot 'Source\ImageImportPolicy.cs')
    (Join-Path $projectRoot 'Source\HeatTileTextureLibrary.cs')
    (Join-Path $projectRoot 'Source\ReforgedBackend.cs')
    (Join-Path $projectRoot 'Source\ModuleHeatTileOverlay.cs')
    (Join-Path $projectRoot 'Source\ReforgedPaintBridge.cs')
    (Join-Path $projectRoot 'Source\HeatTilePainter.cs')
)

& $compiler $arguments

if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE" }
Write-Output $output
