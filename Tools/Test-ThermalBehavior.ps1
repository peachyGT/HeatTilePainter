$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('HeatTileThermal-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & (Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe') /nologo /target:exe ('/out:' + $temp) (Join-Path $root 'Source\ThermalProtectionPolicy.cs') (Join-Path $PSScriptRoot 'ThermalBehaviorTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Thermal behavior fixture compilation failed.' }
    & $temp
    if ($LASTEXITCODE -ne 0) { throw 'Thermal behavior fixture failed.' }
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
