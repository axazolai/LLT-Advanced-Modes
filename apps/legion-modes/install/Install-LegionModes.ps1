#requires -Version 7.0
# Installs LegionModes next to Lenovo Legion Toolkit. Elevated. Idempotent: a step whose result is already in place
# is logged as skipped. Exit code 0 only when every step is done or skipped; the first failed step stops the run (exit 1).
# LLT's version, asset, sha256 and signer come from llt.lock.json beside this script. LLT is installed only when it is absent;
# an LLT that is present is moved by the keeper's update flow. The plugin goes into LLT's folder only when the installed LLT's
# version equals the package's; plugin\<version>\ is always copied to C:\Program Files\LegionModes\plugin\<version>\.
# Log: <DataRoot>\install.log. The last action, after every step succeeded, writes C:\Program Files\LegionModes\install-complete.txt with the
# payload hash (zzz-payload.txt of the staged folder); the steps file verifies on it.
# Layout of -SourceDir (the staged depot folder):
#   bin\plugin  bin\keeper  bin\guard  bin\quick  plugin\<llt version>  innounp-2.zip  llt.lock.json  <LLT installer>  zzz-payload.txt
param([string]$SourceDir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'

$Lock         = Get-Content -LiteralPath (Join-Path $SourceDir 'llt.lock.json') -Raw | ConvertFrom-Json
$LltVersion   = [version]$Lock.llt.version
$LltInstaller = $Lock.llt.asset
$LltSha256    = $Lock.llt.sha256.ToLowerInvariant()
$LltSigner    = $Lock.llt.signerThumbprint
$LltExe      = 'C:\Program Files\LenovoLegionToolkit\Lenovo Legion Toolkit.exe'
$LltProcess   = 'Lenovo Legion Toolkit'
$AutorunTask  = 'LenovoLegionToolkit_Autorun_6efcc882-924c-4cbc-8fec-f45c25696f98'
$InstallDir   = 'C:\Program Files\LegionModes'
$KeeperExe    = Join-Path $InstallDir 'LegionModes.Keeper.exe'
$QuickExe     = Join-Path $InstallDir 'LegionModes.Quick.exe'
$GuardExe     = Join-Path $InstallDir 'LegionModes.Guard.exe'
$KeeperTask   = 'LegionModes Keeper'
# The elevated keeper starts through cmd with the .NET host variables cleared: a non-elevated process can set them in HKCU\Environment
# and make the keeper load foreign code (R-1607). cmd.exe is not a .NET program.
$KeeperCmd    = 'cmd.exe'
$KeeperCmdArg = '/d /s /c "set DOTNET_ROOT=& set DOTNET_ROOT_X64=& set DOTNET_ROOT(x86)=& set DOTNET_ADDITIONAL_DEPS=& set DOTNET_SHARED_STORE=& set DOTNET_STARTUP_HOOKS=& set COREHOST_TRACE=& "' + $KeeperExe + '""'
$GuardService = 'LegionModesGuard'
$EventSource  = 'LegionModes'
$PluginPipe   = 'LegionModes.Plugin'
$DataRoot     = if (Test-Path 'D:\') { 'D:\ProgramData\LegionModes' } else { Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'LegionModes' }
$LogFile      = Join-Path $DataRoot 'install.log'
$Marker       = Join-Path $InstallDir 'install-complete.txt'

function Write-Log([string]$text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $text
    Write-Host $line
    try {
        Initialize-DataRoot
        Add-Content -LiteralPath $LogFile -Value $line -Encoding utf8
    } catch { Write-Host "log write failed: $($_.Exception.Message)" }
}

function Get-Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# Files of $src whose copy under $dst is missing or different.
function Get-FolderDiff([string]$src, [string]$dst) {
    $srcFull = (Resolve-Path -LiteralPath $src).ProviderPath.TrimEnd('\')
    foreach ($f in Get-ChildItem -LiteralPath $srcFull -Recurse -File) {
        $rel = $f.FullName.Substring($srcFull.Length + 1)
        $to = Join-Path $dst $rel
        if (-not (Test-Path -LiteralPath $to -PathType Leaf) -or (Get-Sha $f.FullName) -ne (Get-Sha $to)) { $rel }
    }
}

function Copy-FolderDiff([string]$src, [string]$dst, [string[]]$rel) {
    foreach ($r in $rel) {
        $to = Join-Path $dst $r
        New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath (Join-Path $src $r) -Destination $to -Force
    }
}

# Files under $dst that no source folder holds (relative paths); $skipUnder is a subfolder of $dst left alone.
function Get-FolderExtra([string[]]$srcs, [string]$dst, [string[]]$skipUnder = @()) {
    if (-not (Test-Path -LiteralPath $dst)) { return }
    $dstFull = (Resolve-Path -LiteralPath $dst).ProviderPath.TrimEnd('\')
    $want = @{}
    foreach ($s in $srcs) {
        $sFull = (Resolve-Path -LiteralPath $s).ProviderPath.TrimEnd('\')
        foreach ($f in Get-ChildItem -LiteralPath $sFull -Recurse -File) { $want[$f.FullName.Substring($sFull.Length + 1).ToLowerInvariant()] = $true }
    }
    foreach ($f in Get-ChildItem -LiteralPath $dstFull -Recurse -File) {
        $rel = $f.FullName.Substring($dstFull.Length + 1)
        if ($skipUnder | Where-Object { $rel.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }) { continue }
        if (-not $want.ContainsKey($rel.ToLowerInvariant())) { $rel }
    }
}

# Each extra file is removed by its own name; no recursive delete.
function Remove-FolderExtra([string]$dst, [string[]]$rel) {
    foreach ($r in $rel) { Remove-Item -LiteralPath (Join-Path $dst $r) -Force }
}

# Deletes a temp tree the script created, file by file and folder by folder, each by its own name.
function Remove-NamedTree([string]$root) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -Force) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue }
    foreach ($d in (Get-ChildItem -LiteralPath $root -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)) { Remove-Item -LiteralPath $d.FullName -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $root -Force -ErrorAction SilentlyContinue
}

function Get-ConsoleUser {
    $name = (Get-CimInstance Win32_ComputerSystem).UserName
    if ($name) { return $name }
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $current.IsSystem) { return $current.Name }
    return $null
}

# Account name -> SID; the task's principal comes back as the bare name ("Axa"), the console user as "PC\Axa".
function Get-Sid([string]$name) {
    if (-not $name) { return '' }
    try { (New-Object Security.Principal.NTAccount($name)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $name }
}

# LOCALAPPDATA of the interactive user (the elevated account may be SYSTEM or another administrator).
function Get-UserLocalAppData([string]$user) {
    $sid = (New-Object Security.Principal.NTAccount($user)).Translate([Security.Principal.SecurityIdentifier]).Value
    $profile = Get-CimInstance Win32_UserProfile -Filter "SID = '$sid'"
    if (-not $profile) { throw "no profile found for $user ($sid)" }
    Join-Path $profile.LocalPath 'AppData\Local'
}

# ExitRequest to the plugin pipe: 4-byte little-endian length, then {"type":"ExitRequest","body":{}}.
function Send-ExitRequest {
    try {
        $client = New-Object IO.Pipes.NamedPipeClientStream('.', $PluginPipe, [IO.Pipes.PipeDirection]::InOut)
        try {
            $client.Connect(2000)
            $json = [Text.Encoding]::UTF8.GetBytes('{"type":"ExitRequest","body":{}}')
            $buf = [BitConverter]::GetBytes([int]$json.Length) + $json
            $client.Write($buf, 0, $buf.Length); $client.Flush()
            return $true
        } finally { $client.Dispose() }
    } catch { return $false }
}

function Stop-Llt {
    $procs = @(Get-Process -Name $LltProcess -ErrorAction SilentlyContinue)
    if (-not $procs) { return $false }
    if (Send-ExitRequest) { Write-Log 'LLT: ExitRequest sent to the plugin' }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name $LltProcess -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
    $left = @(Get-Process -Name $LltProcess -ErrorAction SilentlyContinue)
    if ($left) { Write-Log 'LLT: still running after 10 s, Stop-Process'; $left | Stop-Process -Force; Start-Sleep -Seconds 1 }
    return $true
}

# The keeper restarts LLT and the guard restarts the keeper, so both stop before anything under them changes.
$script:supervisionStopped = $false
function Stop-Supervision {
    if ($script:supervisionStopped) { return }
    $script:supervisionStopped = $true
    $svc = Get-Service -Name $GuardService -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $GuardService -Force; Write-Log "supervision: service $GuardService stopped" }
    if (Get-ScheduledTask -TaskName $KeeperTask -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $KeeperTask -ErrorAction SilentlyContinue }
    $keeper = @(Get-Process -Name 'LegionModes.Keeper' -ErrorAction SilentlyContinue)
    if ($keeper) { $keeper | Stop-Process -Force; Write-Log 'supervision: keeper process stopped'; Start-Sleep -Seconds 1 }
}

# After a failure the supervision that was stopped goes back up, whatever state the files are in.
function Restart-Supervision {
    if (-not $script:supervisionStopped) { return }
    try {
        if (Get-Service -Name $GuardService -ErrorAction SilentlyContinue) { Start-Service -Name $GuardService -ErrorAction Stop; Write-Log "supervision: service $GuardService started again" }
        if (Get-ScheduledTask -TaskName $KeeperTask -ErrorAction SilentlyContinue) { Start-ScheduledTask -TaskName $KeeperTask; Write-Log 'supervision: keeper task started again' }
    } catch { Write-Log "supervision: restart failed: $($_.Exception.Message)" }
}

# The protected DACL the keeper applies (DataRootSecurity.Apply): SYSTEM and Administrators FullControl, Users ReadAndExecute,
# inherited by children, no inherited ACEs.
function Get-DataRootAcl {
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $fs = [Security.AccessControl.FileSystemRights]
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($e in @(
            @([Security.Principal.WellKnownSidType]::LocalSystemSid, $fs::FullControl),
            @([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $fs::FullControl),
            @([Security.Principal.WellKnownSidType]::BuiltinUsersSid, $fs::ReadAndExecute))) {
        $sid = New-Object Security.Principal.SecurityIdentifier($e[0], $null)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, $e[1], $inherit, $none, $allow)))
    }
    return $acl
}

# The data root is secured before anything is written into it: the keeper runs installers kept there. A folder somebody else
# created first (a different owner keeps the right to rewrite its DACL) is refused.
$script:DataRootReady = $false
function Initialize-DataRoot {
    if ($script:DataRootReady) { return }
    if (-not (Test-Path -LiteralPath $DataRoot)) { New-Item -ItemType Directory -Force $DataRoot | Out-Null }
    $trusted = @('S-1-5-18', 'S-1-5-32-544')
    $owner = (Get-Acl -LiteralPath $DataRoot).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $trusted) {
        $acl = Get-DataRootAcl
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        try { Set-Acl -LiteralPath $DataRoot -AclObject $acl } catch { }
        $owner = (Get-Acl -LiteralPath $DataRoot).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $trusted) { throw "the data root $DataRoot is owned by $owner, not by SYSTEM or Administrators; remove the folder and run the installer again" }
    }
    Set-Acl -LiteralPath $DataRoot -AclObject (Get-DataRootAcl)
    $script:DataRootReady = $true
}

function Get-AclKey($acl) {
    $rules = $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        ForEach-Object { '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.IdentityReference.Value, ([int]$_.FileSystemRights -band (-bnot 0x100000)), [int]$_.InheritanceFlags, [int]$_.PropagationFlags, $_.AccessControlType, $_.IsInherited }
    ('protected=' + $acl.AreAccessRulesProtected + ';' + (($rules | Sort-Object) -join ';'))
}

# Patches only the one top-level key of an LLT settings file in place (the rest of the text stays byte for byte); the first
# change of a file keeps <file>.bak. Returns $true when the file changed. A result that does not parse is never written (the function throws first);
# a failure of the check after the write also throws, and the file keeps the patched text.
function Set-JsonSetting([string]$file, [string]$key, $value) {
    if (Test-JsonSetting $file $key $value) { return $false }
    $literal = if ($value -is [bool]) { $value.ToString().ToLowerInvariant() } else { '"' + $value + '"' }
    $exists = Test-Path -LiteralPath $file
    $raw = if ($exists) { [IO.File]::ReadAllText($file) } else { '' }
    $rx = '("' + [regex]::Escape($key) + '"\s*:\s*)("(?:[^"\\]|\\.)*"|-?[0-9.]+|true|false|null)'
    if ($raw -cmatch $rx) { $new = ([regex]$rx).Replace($raw, [Text.RegularExpressions.MatchEvaluator]{ param($m) $m.Groups[1].Value + $literal }, 1) }
    elseif ($raw.Trim() -eq '' -or $raw.Trim() -match '^\{\s*\}$') { $new = "{`n  `"$key`": $literal`n}" }
    elseif ($raw -match '^\s*\{') { $new = ([regex]'^(\s*)\{').Replace($raw, [Text.RegularExpressions.MatchEvaluator]{ param($m) $m.Groups[1].Value + "{`n  `"$key`": $literal," }, 1) }
    else { throw "$file is not a JSON object" }
    try { $null = $new | ConvertFrom-Json -AsHashtable } catch { throw "patched $file would not parse: $($_.Exception.Message)" }
    New-Item -ItemType Directory -Force (Split-Path -Parent $file) | Out-Null
    if ($exists -and -not (Test-Path -LiteralPath "$file.bak")) { Copy-Item -LiteralPath $file -Destination "$file.bak" }
    [IO.File]::WriteAllText($file, $new, (New-Object Text.UTF8Encoding($false)))
    if (-not (Test-JsonSetting $file $key $value)) { throw "$file does not hold $key after the patch" }
    return $true
}

function Test-JsonSetting([string]$file, [string]$key, $value) {
    if (-not (Test-Path -LiteralPath $file)) { return $false }
    try { $o = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable } catch { return $false }
    return ($o -is [hashtable] -and $o.ContainsKey($key) -and $o[$key] -ceq $value)
}

# Service failure actions are read from the registry (sc.exe output is localized).
function Test-FailureConfig {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$GuardService"
    $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    if (-not $item -or -not $item.FailureActions -or $item.FailureActionsOnNonCrashFailures -ne 1) { return $false }
    $b = [byte[]]$item.FailureActions
    if ($b.Length -lt 20 + 24) { return $false }
    if ([BitConverter]::ToUInt32($b, 0) -ne 86400) { return $false }
    $count = [BitConverter]::ToUInt32($b, 12)
    if ($count -ne 3) { return $false }
    $want = 10000, 30000, 60000
    for ($i = 0; $i -lt 3; $i++) {
        $o = 20 + 8 * $i
        if ([BitConverter]::ToUInt32($b, $o) -ne 1 -or [BitConverter]::ToUInt32($b, $o + 4) -ne $want[$i]) { return $false }
    }
    return $true
}

# Runs `$exe $arguments` once in the owner's interactive session: a scheduled task for that user, started at once and removed
# after it ends. Throws when it does not finish in 60 s or ends with a non-zero code.
function Invoke-InOwnerSession([string]$user, [string]$exe, [string]$arguments) {
    $taskName = 'LegionModes Once ' + [guid]::NewGuid().ToString('N')
    $action    = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory (Split-Path -Parent $exe)
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddSeconds(60)
        do { Start-Sleep -Milliseconds 500; $info = Get-ScheduledTaskInfo -TaskName $taskName; $state = (Get-ScheduledTask -TaskName $taskName).State }
        while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        if ($state -eq 'Running') { throw "$exe $arguments did not finish in 60 s" }
        if ($info.LastTaskResult -ne 0) { throw "$exe $arguments ended with code $($info.LastTaskResult)" }
    } finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
}

function Invoke-Step([string]$name, [scriptblock]$body) {
    try {
        $r = & $body
        Write-Log ("[{0}] {1}" -f $name, $(if ($r) { $r } else { 'done' }))
    } catch {
        Write-Log "[$name] FAILED: $($_.Exception.Message)"
        Restart-Supervision
        Write-Log 'install stopped: exit 1'
        exit 1
    }
}

Write-Log "install start (SourceDir $SourceDir)"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Log 'FAILED: run elevated'; exit 1
}
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).ProviderPath
if (Test-Path -LiteralPath $Marker) { Remove-Item -LiteralPath $Marker -Force; Write-Log 'completion marker removed: the install is not complete until the last step' }

# Step 0: .NET 9 runtimes.
Invoke-Step '0 dotnet runtimes' {
    foreach ($rt in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
        $dir = Join-Path 'C:\Program Files\dotnet\shared' $rt
        if (-not (Test-Path -LiteralPath $dir) -or -not (Get-ChildItem -LiteralPath $dir -Directory | Where-Object { $_.Name -like '9.*' })) {
            throw "the .NET 9 runtime $rt is missing under $dir"
        }
    }
    'skipped (both .NET 9 runtimes are present)'
}

# What has to change, decided before anything is stopped.
$pluginSrc = Join-Path $SourceDir 'bin\plugin'
$keeperSrc = Join-Path $SourceDir 'bin\keeper'
$guardSrc  = Join-Path $SourceDir 'bin\guard'
$quickSrc  = Join-Path $SourceDir 'bin\quick'
$verSrc    = Join-Path $SourceDir "plugin\$LltVersion"
$verDst    = Join-Path $InstallDir "plugin\$LltVersion"
foreach ($p in $pluginSrc, $keeperSrc, $guardSrc, $quickSrc, $verSrc, (Join-Path $SourceDir 'innounp-2.zip'), (Join-Path $SourceDir $LltInstaller), (Join-Path $SourceDir 'zzz-payload.txt')) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Log "FAILED: payload missing: $p"; exit 1 }
}
$user = Get-ConsoleUser
if (-not $user) { Write-Log 'FAILED: no interactive user to install the plugin and the keeper task for'; exit 1 }
$localAppData = Get-UserLocalAppData $user
$lltData   = Join-Path $localAppData 'LenovoLegionToolkit'
$pluginDst = Join-Path $lltData 'Plugins\LegionModes'
Write-Log "interactive user $user, LLT data $lltData"

$innounpTmp = Join-Path ([IO.Path]::GetTempPath()) ('legionmodes-innounp-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $innounpTmp | Out-Null
Expand-Archive -LiteralPath (Join-Path $SourceDir 'innounp-2.zip') -DestinationPath $innounpTmp -Force
$innounpDst = Join-Path $InstallDir 'innounp'

$lltFile    = if (Test-Path -LiteralPath $LltExe) { [version](Get-Item -LiteralPath $LltExe).VersionInfo.FileVersionRaw } else { $null }
$needLlt    = -not $lltFile                                   # an LLT that is present is moved by the update flow
$pluginFits = $needLlt -or ($lltFile -eq $LltVersion)         # the plugin goes into LLT's folder only for LLT of the package's version
$pluginDiff = if ($pluginFits) { @(Get-FolderDiff $pluginSrc $pluginDst) } else { @() }
$keeperDiff = @(Get-FolderDiff $keeperSrc $InstallDir)
$guardDiff  = @(Get-FolderDiff $guardSrc $InstallDir)
$quickDiff  = @(Get-FolderDiff $quickSrc $InstallDir)
$verDiff    = @(Get-FolderDiff $verSrc $verDst)
$verExtra   = @(Get-FolderExtra @($verSrc) $verDst)
$innounpDiff = @(Get-FolderDiff $innounpTmp $innounpDst)
$pluginExtra  = if ($pluginFits) { @(Get-FolderExtra @($pluginSrc) $pluginDst) } else { @() }
$installExtra = @(Get-FolderExtra @($keeperSrc, $guardSrc, $quickSrc) $InstallDir @('innounp', 'plugin'))
$innounpExtra = @(Get-FolderExtra @($innounpTmp) $innounpDst)
$settingsDirty = -not (Test-JsonSetting (Join-Path $lltData 'update_settings.json') 'UpdateCheckFrequency' 'Never') -or
                 -not (Test-JsonSetting (Join-Path $lltData 'integrations.json') 'CLI' $true)
$autorunPresent = [bool](Get-ScheduledTask -TaskName $AutorunTask -ErrorAction SilentlyContinue)
$dirty = $needLlt -or $pluginDiff.Count -or $keeperDiff.Count -or $guardDiff.Count -or $quickDiff.Count -or $verDiff.Count -or $verExtra.Count -or $innounpDiff.Count -or $pluginExtra.Count -or $installExtra.Count -or $innounpExtra.Count -or $settingsDirty -or $autorunPresent
if ($dirty) { Stop-Supervision }

# Step 1: LLT stopped.
Invoke-Step '1 LLT stopped' {
    if (-not $dirty) { return 'skipped (nothing to change)' }
    if (Stop-Llt) { 'done (LLT stopped)' } else { 'skipped (LLT not running)' }
}

# Step 2: LLT, only when absent (LLT is stopped first).
Invoke-Step '2 LLT' {
    $installerPath = Join-Path $SourceDir $LltInstaller
    if ((Get-Sha $installerPath) -ne $LltSha256) { throw "bundled LLT installer hash differs from the pinned $LltSha256" }
    $keep = Join-Path $DataRoot "installers\$LltVersion"
    $kept = Join-Path $keep $LltInstaller
    $note = ''
    if ($needLlt) {
        if ((Get-AuthenticodeSignature -LiteralPath $installerPath).SignerCertificate.Thumbprint -ne $LltSigner) { throw 'bundled LLT installer signer differs from the pinned one' }
        $p = Start-Process -FilePath $installerPath -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-' -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "LLT installer exit code $($p.ExitCode)" }
        $now = [version](Get-Item -LiteralPath $LltExe).VersionInfo.FileVersionRaw
        if ($now -ne $LltVersion) { throw "LLT version after the install is $now, expected $LltVersion" }
        if (Stop-Llt) { Write-Log 'LLT: stopped again after the installer started it' }
        $note = "installed $LltVersion (was $(if ($lltFile) { $lltFile } else { 'absent' }))"
    }
    $lltNow = if (Test-Path -LiteralPath $LltExe) { [version](Get-Item -LiteralPath $LltExe).VersionInfo.FileVersionRaw } else { $null }
    if ($lltNow -eq $LltVersion -and (-not (Test-Path -LiteralPath $kept) -or (Get-Sha $kept) -ne $LltSha256)) {
        New-Item -ItemType Directory -Force $keep | Out-Null
        Copy-Item -LiteralPath $installerPath -Destination $kept -Force
        $note = ($note + ' installer copy kept in ' + $keep).Trim()
    }
    if ($note) { $note } elseif ($lltNow -eq $LltVersion) { "skipped (LLT $LltVersion present, installer copy kept)" } else { "skipped (LLT $lltNow present; the update flow moves it to $LltVersion)" }
}

# Step 3: plugin.
Invoke-Step '3 plugin' {
    if (-not $pluginFits) { return "skipped (LLT $lltFile differs from the package's $LltVersion; the update flow installs the plugin)" }
    if ($pluginDiff.Count -eq 0 -and $pluginExtra.Count -eq 0) { return 'skipped (plugin folder mirrors the payload)' }
    Copy-FolderDiff $pluginSrc $pluginDst $pluginDiff
    Remove-FolderExtra $pluginDst $pluginExtra
    "done (copied: $($pluginDiff -join ', '); removed: $($pluginExtra -join ', ')) -> $pluginDst"
}

# Step 3b: data root, its DACL, innounp.
Invoke-Step '3b data root' {
    Initialize-DataRoot
    $want = Get-DataRootAcl
    $have = Get-Acl -LiteralPath $DataRoot
    if ((Get-AclKey $want) -eq (Get-AclKey $have)) { $r = "skipped (DACL of $DataRoot in place)" }
    else { Set-Acl -LiteralPath $DataRoot -AclObject $want; $r = "done (protected DACL applied to $DataRoot)" }
    if ($innounpDiff.Count -or $innounpExtra.Count) {
        Copy-FolderDiff $innounpTmp $innounpDst $innounpDiff
        Remove-FolderExtra $innounpDst $innounpExtra
        $r += "; innounp placed in $innounpDst"
    } else { $r += '; innounp skipped (identical)' }
    $r
}
Remove-NamedTree $innounpTmp

# Step 4: LLT settings.
Invoke-Step '4 LLT settings' {
    $notes = @()
    if (Set-JsonSetting (Join-Path $lltData 'update_settings.json') 'UpdateCheckFrequency' 'Never') { $notes += 'UpdateCheckFrequency=Never' }
    if (Set-JsonSetting (Join-Path $lltData 'integrations.json') 'CLI' $true) { $notes += 'CLI=true' }
    if (Get-ScheduledTask -TaskName $AutorunTask -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $AutorunTask -Confirm:$false
        $notes += 'LLT autorun task deleted'
    }
    if ($notes) { 'done (' + ($notes -join ', ') + ')' } else { 'skipped (settings in place)' }
}

# Step 5: keeper, guard, quick, and the plugin for this LLT version in plugin\<version>\.
Invoke-Step '5 keeper, guard, quick' {
    $n = $keeperDiff.Count + $guardDiff.Count + $quickDiff.Count + $verDiff.Count
    if ($n -eq 0 -and $installExtra.Count -eq 0 -and $verExtra.Count -eq 0) { return "skipped ($InstallDir mirrors the payload)" }
    Copy-FolderDiff $keeperSrc $InstallDir $keeperDiff
    Copy-FolderDiff $guardSrc $InstallDir $guardDiff
    Copy-FolderDiff $quickSrc $InstallDir $quickDiff
    Copy-FolderDiff $verSrc $verDst $verDiff
    Remove-FolderExtra $InstallDir $installExtra
    Remove-FolderExtra $verDst $verExtra
    "done ($n file(s) copied, $($installExtra.Count) removed in $InstallDir)"
}

# Step 5b: the install folder on the machine PATH, so the keeper is always started from the one path its tray icons are tied to.
Invoke-Step '5b machine path' {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $entries = @($machine -split ';' | Where-Object { $_ })
    if ($entries | Where-Object { $_.TrimEnd('\') -ieq $InstallDir }) { return "skipped ($InstallDir is on the machine PATH)" }
    [Environment]::SetEnvironmentVariable('Path', (($entries + $InstallDir) -join ';'), 'Machine')
    "done ($InstallDir added to the machine PATH)"
}

# Step 6: keeper task.
Invoke-Step '6 keeper task' {
    $t = Get-ScheduledTask -TaskName $KeeperTask -ErrorAction SilentlyContinue
    if ($t) {
        $a = @($t.Actions)[0]; $trg = @($t.Triggers)[0]
        $same = ($a.Execute -eq $KeeperCmd) -and ($a.Arguments -eq $KeeperCmdArg) -and ((Get-Sid $t.Principal.UserId) -eq (Get-Sid $user)) -and ($t.Principal.RunLevel -eq 'Highest') -and
                ($trg -and $trg.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and (Get-Sid $trg.UserId) -eq (Get-Sid $user)) -and
                ($t.Settings.RestartCount -eq 3) -and ($t.Settings.RestartInterval -eq 'PT1M') -and ($t.Settings.ExecutionTimeLimit -eq 'PT0S')
        if ($same) { return 'skipped (task in place)' }
    }
    $action    = New-ScheduledTaskAction -Execute $KeeperCmd -Argument $KeeperCmdArg -WorkingDirectory $InstallDir
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $KeeperTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "done (task for $user)"
}

# Step 7: event source, guard service.
Invoke-Step '7 event source and guard service' {
    $notes = @()
    if (-not [Diagnostics.EventLog]::SourceExists($EventSource)) {
        [Diagnostics.EventLog]::CreateEventSource($EventSource, 'Application'); $notes += "event source $EventSource created"
    }
    $svc = Get-CimInstance Win32_Service -Filter "Name = '$GuardService'"
    $quoted = '"' + $GuardExe + '"'
    $pathOk = $svc -and ($svc.PathName -eq $quoted -or $svc.PathName -eq $GuardExe)
    if ($svc -and (-not $pathOk -or $svc.StartMode -ne 'Auto')) {
        & sc.exe config $GuardService binPath= $quoted start= auto | Out-Null
        if ($LASTEXITCODE) { throw "sc config exit $LASTEXITCODE" }
        $notes += 'service reconfigured'
    } elseif (-not $svc) {
        New-Service -Name $GuardService -BinaryPathName $quoted -DisplayName 'LegionModes Guard' -StartupType Automatic | Out-Null
        $notes += 'service created'
    }
    if (-not (Test-FailureConfig)) {
        & sc.exe failure $GuardService reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
        if ($LASTEXITCODE) { throw "sc failure exit $LASTEXITCODE" }
        & sc.exe failureflag $GuardService 1 | Out-Null
        if ($LASTEXITCODE) { throw "sc failureflag exit $LASTEXITCODE" }
        $notes += 'failure actions set'
    }
    if ((Get-Service -Name $GuardService).Status -ne 'Running') {
        Start-Service -Name $GuardService; $notes += 'service started'
    }
    if ($notes) { 'done (' + ($notes -join ', ') + ')' } else { 'skipped (source, service and failure actions in place; service running)' }
}

# Step 7b: the desktop menu, registered by LegionModes.Quick.exe in the owner's session (a scheduled task run once as the owner).
Invoke-Step '7b desktop menu' {
    Invoke-InOwnerSession $user $QuickExe '--register'
    "done (--register ran as $user)"
}

# Step 8: keeper task started (the keeper starts LLT).
Invoke-Step '8 keeper started' {
    if (Get-Process -Name 'LegionModes.Keeper' -ErrorAction SilentlyContinue) { return 'skipped (keeper running)' }
    Start-ScheduledTask -TaskName $KeeperTask
    'done (keeper task started)'
}

Invoke-Step '9 completion marker' {
    $sha = ([IO.File]::ReadAllText((Join-Path $SourceDir 'zzz-payload.txt'))).Trim().ToUpperInvariant()
    [IO.File]::WriteAllText($Marker, $sha, (New-Object Text.UTF8Encoding($false)))
    "done ($Marker = $sha)"
}
Write-Log 'install finished: exit 0'
exit 0
