#requires -Version 7.0
# Stages the LegionModes payload into depot/software/legion-modes/ (git-ignored except the steps file) and checks
# every staged file against its record in depot/software/cache-manifest.json.
# Outside the project that hosts the depot (no depot manifest, no steps file) those two parts are skipped: the payload and its hash are still written.
# The pinned downloads (LLT installer, innounp) are checked against the lock; the build record `legion-modes`
# (the keeper dll) is refreshed from the staged file.
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Ref,
    [Parameter(Mandatory)]$Lock
)
$ErrorActionPreference = 'Stop'
$app      = (Resolve-Path "$PSScriptRoot\..").Path
$dotnet   = Join-Path $Root '_work\dotnet\dotnet.exe'
$dest     = Join-Path $Root 'depot\software\legion-modes'
$bin      = Join-Path $dest 'bin'
$dl       = Join-Path $Root '_work\llt-download'
$manifest = Join-Path $Root 'depot\software\cache-manifest.json'

function Get-Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant() }

New-Item -ItemType Directory -Force $dest | Out-Null
foreach ($p in ('keeper', 'guard', 'quick', 'plugin' | ForEach-Object { Join-Path $bin $_ }) + (Join-Path $dest 'plugin')) {
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
New-Item -ItemType Directory -Force $bin | Out-Null

# Keeper and guard: framework-dependent publish, one folder each.
foreach ($name in 'Keeper', 'Guard', 'Quick') {
    $out = Join-Path $bin $name.ToLowerInvariant()
    & $dotnet publish "$app\src\LegionModes.$name\LegionModes.$name.csproj" -c Release --no-self-contained -nologo "-p:LltRef=$Ref" -o $out
    if ($LASTEXITCODE) { throw "publish $name failed" }
}

# Keeper, guard and quick share one install folder: a file name present in two must hold the same bytes.
foreach ($pair in @(('guard', 'keeper'), ('quick', 'keeper'), ('quick', 'guard'))) {
    $a = Join-Path $bin $pair[0]; $b = Join-Path $bin $pair[1]
    foreach ($f in Get-ChildItem $a -Recurse -File) {
        $rel = $f.FullName.Substring($a.Length + 1)
        $twin = Join-Path $b $rel
        if ((Test-Path -LiteralPath $twin) -and (Get-Sha $twin) -ne (Get-Sha $f.FullName)) { throw "$($pair[0]) and $($pair[1]) publish different bytes for $rel" }
    }
}

# Plugin: exactly the two DLLs and their pdbs from the plugin build output.
$pluginProj = "$app\src\LegionModes.Plugin\LegionModes.Plugin.csproj"
$outDir = ((& $dotnet msbuild $pluginProj -nologo -p:Configuration=Release -p:Platform=x64 "-p:LltRef=$Ref" -getProperty:OutDir) -join '').Trim()
if (-not [IO.Path]::IsPathRooted($outDir)) { $outDir = Join-Path "$app\src\LegionModes.Plugin" $outDir }
$pluginDst = Join-Path $bin 'plugin'
New-Item -ItemType Directory -Force $pluginDst | Out-Null
foreach ($f in 'LegionModes.dll', 'LegionModes.pdb', 'LegionModes.Core.dll', 'LegionModes.Core.pdb') {
    $src = Join-Path $outDir $f
    if (-not (Test-Path -LiteralPath $src)) { throw "plugin build output lacks $f ($outDir)" }
    Copy-Item -LiteralPath $src -Destination $pluginDst -Force
}

# Translation files travel with the plugin (lang\<code>.json, read by Tr).
$langSrc = Join-Path $outDir 'lang'
if (-not (Test-Path -LiteralPath (Join-Path $langSrc 'en.json'))) { throw "plugin build output lacks lang\en.json ($outDir)" }
$langDst = Join-Path $pluginDst 'lang'
New-Item -ItemType Directory -Force $langDst | Out-Null
Copy-Item -Path (Join-Path $langSrc '*.json') -Destination $langDst -Force

# The same plugin, built for this LLT version, travels to C:\Program Files\LegionModes\plugin\<version>\ for the update flow.
$pluginByVersion = Join-Path $dest "plugin\$($Lock.llt.version)"
New-Item -ItemType Directory -Force $pluginByVersion | Out-Null
Copy-Item -Path (Join-Path $pluginDst '*') -Destination $pluginByVersion -Recurse -Force

# Pinned downloads. An installer of an earlier pin left in the folder is removed.
foreach ($old in Get-ChildItem -LiteralPath $dest -File -Filter 'LenovoLegionToolkitSetup-*.exe') {
    if ($old.Name -ne $Lock.llt.asset) { Remove-Item -LiteralPath $old.FullName -Force }
}
$llt = Join-Path $dl $Lock.llt.asset
$inn = Join-Path $dl 'innounp-2.zip'
if ((Get-Sha $llt) -ne $Lock.llt.sha256.ToUpperInvariant()) { throw 'LLT installer hash differs from the lock' }
if ((Get-Sha $inn) -ne $Lock.innounp.sha256.ToUpperInvariant()) { throw 'innounp hash differs from the lock' }
Copy-Item -LiteralPath $llt -Destination $dest -Force
Copy-Item -LiteralPath $inn -Destination $dest -Force
Copy-Item -LiteralPath "$app\llt.lock.json" -Destination $dest -Force
Copy-Item -LiteralPath "$PSScriptRoot\Install-LegionModes.ps1" -Destination $dest -Force
Copy-Item -LiteralPath "$PSScriptRoot\Uninstall-LegionModes.ps1" -Destination $dest -Force

# The build record follows the staged keeper dll.
if (Test-Path -LiteralPath $manifest) {
    $keeperDll = Join-Path $bin 'keeper\LegionModes.Keeper.dll'
    $text = [IO.File]::ReadAllText($manifest)
    $m = [regex]::Match($text, '(?s)\{\s*"id": "legion-modes",.*?\n    \}')
    if (-not $m.Success) { throw 'cache-manifest.json has no record legion-modes' }
    $ver  = (Get-Item -LiteralPath $keeperDll).VersionInfo.ProductVersion -replace '\+.*$', ''
    $size = [math]::Round((Get-Item -LiteralPath $keeperDll).Length / 1MB, 1).ToString([cultureinfo]::InvariantCulture)
    $sha  = Get-Sha $keeperDll   # the record describes the keeper dll file; the payload hash is separate
    $block = $m.Value
    if ($block -notmatch ('"sha256": "' + $sha + '"')) {
        $block = $block -replace '"sha256": "[0-9A-Fa-f]*"', ('"sha256": "' + $sha + '"')
        $block = $block -replace '"sizeMB": [0-9.]+', ('"sizeMB": ' + $size)
        $block = $block -replace '"version": "[^"]*"', ('"version": "' + $ver + '"')
        $block = $block -replace '"fetched": "[^"]*"', ('"fetched": "' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '"')
        $text = $text.Replace($m.Value, $block)
        $text = [regex]::Replace($text, '"updated": "[^"]*"', ('"updated": "' + (Get-Date -Format 'o') + '"'))
        [IO.File]::WriteAllText($manifest, $text, (New-Object Text.UTF8Encoding($false)))
        Write-Host "cache-manifest.json: record legion-modes refreshed ($sha)"
        Write-Host 'COMMIT depot/software/cache-manifest.json and depot/software/legion-modes/legion-modes.steps.json'
    }
}

# The build hash goes into the payload (last file in full-name order, so a copy cut short fails its verify) and into
# the steps file (both verifies): a rebuilt payload re-applies.
# The payload hash is the SHA-256 over the sorted list of every payload file's relative path and SHA-256 (BUG-006); the two
# files that carry the hash (zzz-payload.txt, the steps file) are not part of it.
$lines = foreach ($f in Get-ChildItem -LiteralPath $dest -Recurse -File) {
    $rel = $f.FullName.Substring($dest.Length + 1).ToLowerInvariant()
    if ($rel -in 'zzz-payload.txt', 'legion-modes.steps.json') { continue }
    "$rel`t$(Get-Sha $f.FullName)"
}
$sorted = [string[]]@($lines)
[Array]::Sort($sorted, [StringComparer]::Ordinal)
$joined = $sorted -join "`n"
$sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($joined))).Replace('-', '')
Write-Host "payload hash $sha over $(@($lines).Count) files"
[IO.File]::WriteAllText((Join-Path $dest 'zzz-payload.txt'), $sha, (New-Object Text.UTF8Encoding($false)))
$stepsPath = Join-Path $dest 'legion-modes.steps.json'
if (Test-Path -LiteralPath $stepsPath) {
    $steps = [IO.File]::ReadAllText($stepsPath)
    $stepsNew = [regex]::Replace($steps, '"pattern": "\^?[0-9A-Fa-f]{64}\$?"', ('"pattern": "' + $sha + '"'))
    if ($stepsNew -ne $steps) {
        [IO.File]::WriteAllText($stepsPath, $stepsNew, (New-Object Text.UTF8Encoding($false)))
        Write-Host 'legion-modes.steps.json: payload hash refreshed; COMMIT it'
    }
}

# Every staged file against its record.
if (Test-Path -LiteralPath $manifest) {
    $records = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).files | Where-Object { $_.id -like 'legion-modes*' }
    if (@($records).Count -ne 3) { throw "expected 3 legion-modes records, found $(@($records).Count)" }
    foreach ($r in $records) {
        $f = Join-Path (Join-Path $Root 'depot\software') $r.file
        if (-not (Test-Path -LiteralPath $f)) { throw "record $($r.id): $($r.file) is not staged" }
        if ((Get-Sha $f) -ne $r.sha256.ToUpperInvariant()) { throw "record $($r.id): sha256 of $($r.file) differs from the record" }
        Write-Host "ok  $($r.id)  $($r.file)"
    }
}
Write-Host "staged into $dest"
