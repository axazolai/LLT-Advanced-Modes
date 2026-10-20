#requires -Version 7.0
# Removes LegionModes: the guard service, the keeper task, C:\Program Files\LegionModes\ and the plugin folder.
# Kept: the data root (backups, update state, install.log) and the plugin configs under Plugins\Configs\LegionModes.
# Elevated. Safe to run twice.
$ErrorActionPreference = 'Stop'

$InstallDir   = 'C:\Program Files\LegionModes'
$KeeperTask   = 'LegionModes Keeper'
$GuardService = 'LegionModesGuard'
$LltProcess   = 'Lenovo Legion Toolkit'
$PluginPipe   = 'LegionModes.Plugin'
$DataRoot     = if (Test-Path 'D:\') { 'D:\ProgramData\LegionModes' } else { Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'LegionModes' }
$LogFile      = Join-Path $DataRoot 'install.log'

function Write-Log([string]$text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} uninstall: {1}' -f (Get-Date), $text
    Write-Host $line
    try {
        if (-not (Test-Path -LiteralPath $DataRoot)) { New-Item -ItemType Directory -Force $DataRoot | Out-Null }
        Add-Content -LiteralPath $LogFile -Value $line -Encoding utf8
    } catch { Write-Host "log write failed: $($_.Exception.Message)" }
}

function Get-ConsoleUser {
    $name = (Get-CimInstance Win32_ComputerSystem).UserName
    if ($name) { return $name }
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $current.IsSystem) { return $current.Name }
    return $null
}

function Get-UserLocalAppData([string]$user) {
    $sid = (New-Object Security.Principal.NTAccount($user)).Translate([Security.Principal.SecurityIdentifier]).Value
    $profile = Get-CimInstance Win32_UserProfile -Filter "SID = '$sid'"
    if (-not $profile) { throw "no profile found for $user ($sid)" }
    Join-Path $profile.LocalPath 'AppData\Local'
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

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Log 'FAILED: run elevated'; exit 1
}
Write-Log 'start'
try {
    # Guard first (it restarts the keeper), then the keeper task and process, then LLT.
    $svc = Get-Service -Name $GuardService -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $GuardService -Force }
        & sc.exe delete $GuardService | Out-Null
        if ($LASTEXITCODE) { throw "sc delete exit $LASTEXITCODE" }
        Write-Log "service $GuardService deleted"
    } else { Write-Log "service $GuardService absent: skipped" }

    if (Get-ScheduledTask -TaskName $KeeperTask -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $KeeperTask -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $KeeperTask -Confirm:$false
        Write-Log "task '$KeeperTask' deleted"
    } else { Write-Log "task '$KeeperTask' absent: skipped" }
    foreach ($name in 'LegionModes.Keeper', 'LegionModes.Guard') {
        $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        if ($procs) { $procs | Stop-Process -Force; Write-Log "process $name stopped" }
    }

    if (Get-Process -Name $LltProcess -ErrorAction SilentlyContinue) {
        if (Send-ExitRequest) { Write-Log 'LLT: ExitRequest sent to the plugin' }
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and (Get-Process -Name $LltProcess -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
        $left = @(Get-Process -Name $LltProcess -ErrorAction SilentlyContinue)
        if ($left) { $left | Stop-Process -Force; Write-Log 'LLT: Stop-Process after 10 s' }
        Write-Log 'LLT left stopped; start it from the Start menu'
    }

    # The desktop menu goes while LegionModes.Quick.exe still exists; it removes the owner's HKCU entries, so it runs in the owner's session.
    $user = Get-ConsoleUser
    $quick = Join-Path $InstallDir 'LegionModes.Quick.exe'
    if ($user -and (Test-Path -LiteralPath $quick)) {
        Invoke-InOwnerSession $user $quick '--unregister'
        Write-Log "desktop menu removed (--unregister ran as $user)"
    } else { Write-Log 'desktop menu: no interactive user or no LegionModes.Quick.exe: skipped' }

    # Wake overrides the plugin recorded in control.json are cleared.
    if ($user) {
        $control = Join-Path (Get-UserLocalAppData $user) 'LenovoLegionToolkit\Plugins\Configs\LegionModes\control.json'
        $names = @()
        if (Test-Path -LiteralPath $control) {
            try { $names = @((Get-Content -LiteralPath $control -Raw | ConvertFrom-Json).WakeOverrides) | Where-Object { $_ } }
            catch { Write-Log "control.json unreadable: $($_.Exception.Message)" }
        }
        foreach ($exe in $names) {
            & powercfg.exe /requestsoverride PROCESS $exe | Out-Null
            Write-Log "wake override for $exe cleared (exit $LASTEXITCODE)"
        }
        if (-not $names) { Write-Log 'no wake overrides recorded' }
    }

    # The marker goes first: a removal that stops half way must not read as installed.
    $marker = Join-Path $InstallDir 'install-complete.txt'
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force; Write-Log 'completion marker removed' }

    # Named folders only.
    if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force; Write-Log "$InstallDir removed" }
    else { Write-Log "$InstallDir absent: skipped" }

    if ($user) {
        $pluginDir = Join-Path (Get-UserLocalAppData $user) 'LenovoLegionToolkit\Plugins\LegionModes'
        if (Test-Path -LiteralPath $pluginDir) { Remove-Item -LiteralPath $pluginDir -Recurse -Force; Write-Log "$pluginDir removed" }
        else { Write-Log "$pluginDir absent: skipped" }
    } else { Write-Log 'no interactive user: the plugin folder was not looked for' }

    Write-Log 'kept: the data root and the plugin configs (Plugins\Configs\LegionModes)'
    Write-Log 'the LLT autorun task was deleted by the install and is not restored: re-enable LLT autorun in LLT itself if wanted'
    Write-Log 'finished: exit 0'
    exit 0
} catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    exit 1
}
