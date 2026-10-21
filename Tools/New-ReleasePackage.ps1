param(
    [string]$Version = '0.25.0'
)

$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))).TrimEnd('\')
$packageRoot = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype'
$distRoot = Join-Path $projectRoot 'dist'
$stageRoot = Join-Path $distRoot ("stage-$Version")
$auditRoot = Join-Path $distRoot ("audit-$Version")
$zipPath = Join-Path $distRoot ("HeatTilePainter-$Version.zip")
$checksumPath = Join-Path $distRoot ("HeatTilePainter-$Version.sha256")
$manifestPath = Join-Path $distRoot ("HeatTilePainter-$Version.manifest.txt")

foreach ($candidate in @($distRoot, $stageRoot, $auditRoot, $zipPath, $checksumPath, $manifestPath)) {
    $resolved = [System.IO.Path]::GetFullPath($candidate)
    if (-not $resolved.StartsWith($projectRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release path escaped the project root: $resolved"
    }
}

$required = @(
    'Assets\ui_icons.png'
    'CustomTextures.cfg.example'
    'ExteriorProfiles.cfg'
    'HeatTileOverlayPrototype.cfg'
    'HeatTilePainter.version'
    'LICENSE.txt'
    'LICENSE-ASSETS.txt'
    'Plugins\HeatTileOverlayPrototype.dll'
    'PluginData\README-Custom-Textures.txt'
    'README.txt'
    'THIRD-PARTY-NOTICES.txt'
)
foreach ($relative in $required) {
    $path = Join-Path $packageRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required package file is missing: $relative"
    }
}

$expectedDll = 'C192218A0C5D05D65ECFB7F1C32440CD78613DD7DF1C62E52089AE3EB6E11074'
$expectedAtlas = '756B51D787B3725D1D6730200A58A86FEDECCB1325BE820AD455CEFD3154DCE8'
$dllHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'Plugins\HeatTileOverlayPrototype.dll') -Algorithm SHA256).Hash
$atlasHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'Assets\ui_icons.png') -Algorithm SHA256).Hash
if ($dllHash -ne $expectedDll) { throw "Release DLL differs from the accepted build: $dllHash" }
if ($atlasHash -ne $expectedAtlas) { throw "Release icon atlas differs from the accepted asset: $atlasHash" }

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
foreach ($path in @($stageRoot, $auditRoot)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
foreach ($path in @($zipPath, $checksumPath, $manifestPath)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

$stagePackage = Join-Path $stageRoot 'GameData\HeatTileOverlayPrototype'
New-Item -ItemType Directory -Path (Split-Path -Parent $stagePackage) -Force | Out-Null
Copy-Item -LiteralPath $packageRoot -Destination $stagePackage -Recurse

# Browser imports are private user content. Never include them even if someone builds a release
# from a working copy that has been used as a live KSP GameData folder.
$stagePluginData = Join-Path $stagePackage 'PluginData'
if (Test-Path -LiteralPath $stagePluginData) {
    Get-ChildItem -LiteralPath $stagePluginData -Filter 'UserTextures*' | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}

Compress-Archive -LiteralPath (Join-Path $stageRoot 'GameData') -DestinationPath $zipPath -CompressionLevel Optimal
Expand-Archive -LiteralPath $zipPath -DestinationPath $auditRoot

$auditedPackage = Join-Path $auditRoot 'GameData\HeatTileOverlayPrototype'
$sourceFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
    $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
} | Where-Object {
    -not $_.StartsWith('PluginData/UserTextures', [System.StringComparison]::OrdinalIgnoreCase)
} | Sort-Object
$archiveFiles = Get-ChildItem -LiteralPath $auditedPackage -Recurse -File | ForEach-Object {
    $_.FullName.Substring($auditedPackage.Length + 1).Replace('\', '/')
} | Sort-Object
if (Compare-Object -ReferenceObject $sourceFiles -DifferenceObject $archiveFiles) {
    throw 'Archive contents differ from the package source tree.'
}

$archiveDllHash = (Get-FileHash -LiteralPath (Join-Path $auditedPackage 'Plugins\HeatTileOverlayPrototype.dll') -Algorithm SHA256).Hash
if ($archiveDllHash -ne $expectedDll) { throw 'Archive DLL hash changed during packaging.' }

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText($checksumPath, "$zipHash  HeatTilePainter-$Version.zip`r`n")
[System.IO.File]::WriteAllLines($manifestPath, $archiveFiles)

Remove-Item -LiteralPath $stageRoot -Recurse -Force
Remove-Item -LiteralPath $auditRoot -Recurse -Force

Write-Output "Release archive: $zipPath"
Write-Output "SHA-256: $zipHash"
Write-Output "Files: $($archiveFiles.Count)"
