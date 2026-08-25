param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        & (Join-Path $projectRoot 'build.ps1')
    }

    & (Join-Path $projectRoot 'Tools\Test-BackendSafety.ps1')
    & (Join-Path $projectRoot 'Tools\Test-ReforgedCoreInvariants.ps1')
    & (Join-Path $projectRoot 'Tools\Test-EventDelegate.ps1')
    & (Join-Path $projectRoot 'Tools\Test-PrecisionAndThermalContracts.ps1')
    & (Join-Path $projectRoot 'Tools\Test-UiRefinementContracts.ps1')
    & (Join-Path $projectRoot 'Tools\Test-FinalOptimizationContracts.ps1')

    Write-Output 'Heat Tile Painter deterministic quality gate passed.'
    Write-Output 'In-game verification may still be required by the active spec.'
}
finally {
    Pop-Location
}
