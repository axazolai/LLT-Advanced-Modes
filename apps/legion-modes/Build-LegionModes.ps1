param([switch]$SkipTests, [switch]$Stage)
$ErrorActionPreference = 'Stop'
$app  = $PSScriptRoot
$root = (Resolve-Path "$app\..\..").Path
$work = Join-Path $root '_work'
$lock = Get-Content "$app\llt.lock.json" -Raw | ConvertFrom-Json
$dotnet = Join-Path $work 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet) -or -not ((& $dotnet --list-sdks) -match '^9\.0\.')) {
    $inst = Join-Path $work 'dotnet-install'; New-Item -ItemType Directory -Force $inst | Out-Null
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$inst\dotnet-install.ps1"
    if ((Get-AuthenticodeSignature "$inst\dotnet-install.ps1").Status -ne 'Valid') { throw 'dotnet-install.ps1 signature' }
    $sdk = (Get-Content "$app\global.json" -Raw | ConvertFrom-Json).sdk.version
    & "$inst\dotnet-install.ps1" -Version $sdk -InstallDir (Join-Path $work 'dotnet') -NoPath
}
function Get-Pinned([string]$url, [string]$sha, [string]$dest) {
    if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null; Invoke-WebRequest $url -OutFile $dest }
    if ((Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -ne $sha) { throw "hash mismatch: $dest" }
}
$dl = Join-Path $work 'llt-download'
Get-Pinned $lock.llt.url $lock.llt.sha256 "$dl\$($lock.llt.asset)"
Get-Pinned $lock.innounp.url $lock.innounp.sha256 "$dl\innounp-2.zip"
$tool = Join-Path $work 'innounp'
if (-not (Test-Path "$tool\innounp.exe")) { Expand-Archive "$dl\innounp-2.zip" $tool -Force }
$ref = Join-Path $work "llt-ref\$($lock.llt.version)"
if (-not (Test-Path "$ref\{app}\LenovoLegionToolkit.Lib.dll")) {
    if ((Get-AuthenticodeSignature "$dl\$($lock.llt.asset)").SignerCertificate.Thumbprint -ne $lock.llt.signerThumbprint) { throw 'LLT signer' }
    & "$tool\innounp.exe" -x -b -q -y -o "-d$ref" "$dl\$($lock.llt.asset)" | Out-Null
}
& $dotnet build "$app\LegionModes.sln" -c Release -nologo -p:LltRef="$ref\{app}"
if ($LASTEXITCODE) { throw 'build failed' }
if (-not $SkipTests) { & $dotnet test "$app\LegionModes.sln" -c Release -nologo --no-build -p:LltRef="$ref\{app}"; if ($LASTEXITCODE) { throw 'tests failed' } }
if ($Stage) { & "$app\install\Stage-Depot.ps1" -Root $root -Ref "$ref\{app}" -Lock $lock }   # Task 15 adds it
