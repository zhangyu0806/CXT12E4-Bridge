[CmdletBinding()]
param(
    [string]$InstallDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Remove-StartupApprovedEntry {
    param(
        [ValidateSet('Run', 'StartupFolder')]
        [string]$SubKey,
        [string]$ValueName
    )

    $path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$SubKey"
    Remove-ItemProperty -LiteralPath $path -Name $ValueName -ErrorAction SilentlyContinue
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $repositoryRoot 'outputs\home-runtime'
}

$executable = Join-Path $InstallDirectory 'Cxt12E4Bridge.exe'
$watchdog = Join-Path $InstallDirectory 'Cxt12E4Watchdog.exe'
$legacyCmdLauncher = Join-Path $InstallDirectory 'Start-Cxt12E4Bridge.cmd'
$legacyPowerShellLauncher = Join-Path $InstallDirectory 'Start-Cxt12E4Bridge.ps1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'Cxt12E4Bridge'
$startupFolder = [Environment]::GetFolderPath('Startup')
$startupShortcutName = 'Cxt12E4BridgeWatchdog.lnk'
$startupShortcutPath = Join-Path $startupFolder $startupShortcutName
$retiredScheduledTaskName = 'Cxt12E4Bridge'
$watchdogScheduledTaskName = 'Cxt12E4BridgeWatchdog'

Remove-ItemProperty -LiteralPath $runKey -Name $runValueName -ErrorAction SilentlyContinue
Remove-StartupApprovedEntry -SubKey 'Run' -ValueName $runValueName
if (Test-Path -LiteralPath $startupShortcutPath -PathType Leaf) {
    Remove-Item -LiteralPath $startupShortcutPath -Force
}
Remove-StartupApprovedEntry -SubKey 'StartupFolder' -ValueName $startupShortcutName
if (Get-Command Unregister-ScheduledTask -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $retiredScheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $watchdogScheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $watchdog -PathType Leaf) {
    $watchdogs = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -eq $watchdog }
    )
    if ($watchdogs.Count -gt 0) {
        $exitWatchdogProcess = Start-Process -FilePath $watchdog -ArgumentList '--watchdog-exit' -Wait -PassThru -WindowStyle Hidden
        $watchdogExitCode = $exitWatchdogProcess.ExitCode
        if ($watchdogExitCode -ne 0) {
            throw "Unable to signal the installed Bridge watchdog (exit code $watchdogExitCode)."
        }

        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $watchdogs = @(
                Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                    Where-Object { $_.ExecutablePath -eq $watchdog }
            )
            if ($watchdogs.Count -eq 0) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ($timer.ElapsedMilliseconds -lt 10000)
        $timer.Stop()

        if ($watchdogs.Count -ne 0) {
            throw "Installed Bridge watchdog did not exit; PID(s): $($watchdogs.ProcessId -join ', ')."
        }
    }
}

if (Test-Path -LiteralPath $executable -PathType Leaf) {
    $running = @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -eq $executable }
    )
    if ($running.Count -gt 0) {
        $exitProcess = Start-Process -FilePath $executable -ArgumentList '--exit' -Wait -PassThru -WindowStyle Hidden
        $exitCode = $exitProcess.ExitCode
        if ($exitCode -ne 0) {
            throw "Unable to signal the installed Bridge (exit code $exitCode)."
        }

        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $running = @(
                Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                    Where-Object { $_.ExecutablePath -eq $executable }
            )
            if ($running.Count -eq 0) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ($timer.ElapsedMilliseconds -lt 10000)
        $timer.Stop()

        if ($running.Count -ne 0) {
            throw "Installed Bridge did not exit; PID(s): $($running.ProcessId -join ', ')."
        }
    }
}

foreach ($file in @(
    $executable,
    $watchdog,
    $legacyCmdLauncher,
    $legacyPowerShellLauncher,
    (Join-Path $InstallDirectory 'README-CXT12E4.md'),
    (Join-Path $InstallDirectory 'install-manifest.json')
)) {
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Remove-Item -LiteralPath $file -Force
    }
}

Write-Host "CXT12E4 Bridge removed from $InstallDirectory."
Write-Host 'Timestamped executable backups and runtime logs were preserved for recovery.'
