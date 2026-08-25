$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$packageRoot = Join-Path $projectRoot 'GameData\HeatTileOverlayPrototype'

$versionFile = Get-Content -Raw -LiteralPath (Join-Path $packageRoot 'HeatTilePainter.version') | ConvertFrom-Json
if ($versionFile.NAME -ne 'Heat Tile Painter' -or
    $versionFile.GITHUB.USERNAME -ne 'peachyGT' -or
    $versionFile.GITHUB.REPOSITORY -ne 'HeatTilePainter' -or
    $versionFile.VERSION.MAJOR -ne 0 -or
    $versionFile.VERSION.MINOR -ne 24 -or
    $versionFile.VERSION.PATCH -ne 1 -or
    $versionFile.KSP_VERSION.MAJOR -ne 1 -or
    $versionFile.KSP_VERSION.MINOR -ne 12 -or
    $versionFile.KSP_VERSION.PATCH -ne 5) {
    throw 'KSP-AVC release identity or compatibility is incorrect.'
}

$expectedDll = 'D3EFDE718014D210911ACE54836A1A0B8F7C8B1816B90280C05D6A2D8C4611CC'
$expectedAtlas = '756B51D787B3725D1D6730200A58A86FEDECCB1325BE820AD455CEFD3154DCE8'
$actualDll = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'Plugins\HeatTileOverlayPrototype.dll') -Algorithm SHA256).Hash
$actualAtlas = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'Assets\ui_icons.png') -Algorithm SHA256).Hash
if ($actualDll -ne $expectedDll) { throw "Accepted DLL identity failed: $actualDll" }
if ($actualAtlas -ne $expectedAtlas) { throw "Accepted icon atlas identity failed: $actualAtlas" }

$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $packageRoot 'Plugins\HeatTileOverlayPrototype.dll'))
$title = $assembly.GetCustomAttributes([Reflection.AssemblyTitleAttribute], $false)[0].Title
$company = $assembly.GetCustomAttributes([Reflection.AssemblyCompanyAttribute], $false)[0].Company
$product = $assembly.GetCustomAttributes([Reflection.AssemblyProductAttribute], $false)[0].Product
if ($title -ne 'Heat Tile Painter' -or $company -ne 'peachyG' -or $product -ne 'Heat Tile Painter') {
    throw 'Compiled assembly public identity is incorrect.'
}

$rootReadme = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'README.md')
$packageReadme = Get-Content -Raw -LiteralPath (Join-Path $packageRoot 'README.txt')
$netkan = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'release\HeatTilePainter.netkan')
$forum = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'release\forum-post.md')
$rootLicense = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'LICENSE')
$packageLicense = Get-Content -Raw -LiteralPath (Join-Path $packageRoot 'LICENSE.txt')
$thirdParty = Get-Content -Raw -LiteralPath (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.txt')
$assetLicense = Get-Content -Raw -LiteralPath (Join-Path $packageRoot 'LICENSE-ASSETS.txt')

foreach ($text in @($rootReadme, $packageReadme, $netkan)) {
    if ($text -notmatch 'ModuleManager') { throw 'A release surface omits the ModuleManager dependency.' }
}
foreach ($text in @($rootLicense, $packageLicense)) {
    if ($text -notmatch 'Copyright \(c\) 2026 peachyG' -or $text -notmatch 'MIT License') {
        throw 'Project MIT license is missing or misattributed.'
    }
}
if ($thirdParty -notmatch 'Reforged Materials Redux' -or $thirdParty -notmatch 'Copyright \(c\) 2026 J4COB') {
    throw 'Reforged third-party attribution is incomplete.'
}
if ($assetLicense -notmatch 'B9-PWings-Modified' -or $assetLicense -notmatch 'Copyright \(c\) 2015') {
    throw 'B9 asset attribution is incomplete.'
}
if ($forum -notmatch 'about 90% vibe-coded' -or
    $forum -match '(?i)author description|placeholder|add the main mod description') {
    throw 'The forum draft is incomplete or omits peachyG''s development acknowledgement.'
}
if ($netkan -notmatch 'find: HeatTileOverlayPrototype' -or
    $netkan -notmatch 'name: ModuleManager' -or
    $netkan -notmatch '#/ckan/github/peachyGT/HeatTilePainter/' -or
    $netkan -match '(?m)^\s*homepage:') {
    throw 'NetKAN install/dependency gate failed, or a forum homepage was added before publication.'
}

$screenshots = @{
    'docs\screenshots\flight-topside.png' = '9FB78E415F3A552FA682C409D56CE63C34E590E9E238AE679DA92108C8D49782'
    'docs\screenshots\flight-underside.png' = '18FED5D3AE787F4B9AC20EE824915DCFBB533C9E6AE815279E33A92711C9DA08'
    'docs\screenshots\upper-atmosphere-underside.png' = 'F1488E537E3CF131989852DF3C3F8B122614BBFE0408B3EDFB006D8A4AB5FD8F'
    'docs\screenshots\editor-ui.png' = 'FB9FCA4F77C6194B02B90E0207EF628D5F582C381F4E0B02420146A4CC99414B'
}
foreach ($entry in $screenshots.GetEnumerator()) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $projectRoot $entry.Key) -Algorithm SHA256).Hash
    if ($actual -ne $entry.Value) { throw "Release screenshot differs from the supplied original: $($entry.Key)" }
}

$privacyPatterns = [ordered]@{
    'Windows user path' = '(?i)(?<![A-Za-z0-9_])[A-Z]:[\\/](?:Users|Documents and Settings)[\\/]'
    'Windows local path' = '(?i)(?<![A-Za-z0-9_])[A-Z]:[\\/](?:Program Files|ProgramData|Windows|Temp|Games|Steam|Soft|Dev|src|work)[\\/]'
    'Unix user path' = '(?i)(?<![A-Za-z0-9_])/(?:home|Users)/[^\s]+'
    'Private key' = ('-----BEGIN ' + '(?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----')
    'GitHub token' = '(?i)(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})'
    'OpenAI token' = '(?i)\bsk-[A-Za-z0-9_-]{20,}'
    'AWS access key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
    'Credential-bearing URL' = '(?i)https?://[^\s/@:]+:[^\s/@]+@'
}

$trackedFiles = @(& git -C $projectRoot ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked files for the public-hygiene scan.' }
foreach ($relative in $trackedFiles) {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $projectRoot $relative))
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $unicode = [System.Text.Encoding]::Unicode.GetString($bytes)
    $printable = (([regex]::Matches($ascii, '[\x20-\x7E]{6,}') | ForEach-Object Value) -join "`n") +
        "`n" + (([regex]::Matches($unicode, '[\x20-\x7E]{6,}') | ForEach-Object Value) -join "`n")
    foreach ($entry in $privacyPatterns.GetEnumerator()) {
        if ([regex]::IsMatch($printable, $entry.Value)) {
            throw "Public-hygiene scan found $($entry.Key) data in $relative."
        }
    }
}

$untracked = @(& git -C $projectRoot ls-files --others --exclude-standard)
if ($untracked.Count -gt 0) {
    throw "Untracked files are outside ignore rules: $($untracked -join ', ')"
}

$credentialRemote = @(& git -C $projectRoot remote -v |
    Select-String -Pattern 'https?://[^\s/@:]+:[^\s/@]+@')
if ($credentialRemote.Count -gt 0) { throw 'A Git remote URL contains embedded credentials.' }

Write-Output 'Public release identity, dependency, licensing, attribution, CKAN, forum, media, and privacy contracts passed.'
