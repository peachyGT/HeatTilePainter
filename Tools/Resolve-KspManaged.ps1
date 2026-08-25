function Get-KspManagedPath {
    [CmdletBinding()]
    param(
        [string]$KspRoot = $env:KSP_ROOT
    )

    if ([string]::IsNullOrWhiteSpace($KspRoot)) {
        throw 'Set KSP_ROOT to the Kerbal Space Program installation directory.'
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($KspRoot).TrimEnd('\', '/')
    $managed = Join-Path $resolvedRoot 'KSP_x64_Data\Managed'
    if (-not (Test-Path -LiteralPath $managed -PathType Container)) {
        throw "KSP managed assemblies not found below KSP_ROOT: $managed"
    }

    return $managed
}
