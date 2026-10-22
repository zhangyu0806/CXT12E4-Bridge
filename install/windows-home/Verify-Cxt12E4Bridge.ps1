[CmdletBinding()]
param(
    [string]$InstallDirectory = '',
    [switch]$SkipAutoStartCheck,
    [switch]$SkipProcessCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Get-WatchdogArgumentString {
    param([string]$BridgeExecutable)

    '--watchdog "{0}"' -f $BridgeExecutable
}

function Test-StartupApprovedEnabled {
    param(
        [ValidateSet('Run', 'StartupFolder')]
        [string]$SubKey,
        [string]$ValueName
    )

    $path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$SubKey"
    $value = (Get-ItemProperty -LiteralPath $path -Name $ValueName -ErrorAction Stop).$ValueName
    if (-not ($value -is [byte[]]) -or $value.Length -lt 1 -or $value[0] -ne 0x02) {
        $hex = if ($value -is [byte[]]) { ($value | ForEach-Object { $_.ToString('X2') }) -join ' ' } else { [string]$value }
        throw "StartupApproved entry '$SubKey/$ValueName' is not enabled: $hex"
    }

    $true
}

function Get-ShortcutInfo {
    param([string]$ShortcutPath)

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    [pscustomobject]@{
        TargetPath = $shortcut.TargetPath
        Arguments = $shortcut.Arguments
        WorkingDirectory = $shortcut.WorkingDirectory
    }
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $repositoryRoot 'outputs\home-runtime'
}

$executable = Join-Path $InstallDirectory 'Cxt12E4Bridge.exe'
$watchdog = Join-Path $InstallDirectory 'Cxt12E4Watchdog.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'Cxt12E4Bridge'
$expectedRunCommand = '"{0}" --watchdog "{1}"' -f $watchdog, $executable
$startupFolder = [Environment]::GetFolderPath('Startup')
$startupShortcutName = 'Cxt12E4BridgeWatchdog.lnk'
$startupShortcutPath = Join-Path $startupFolder $startupShortcutName
$expectedWatchdogArguments = Get-WatchdogArgumentString -BridgeExecutable $executable
$retiredScheduledTaskName = 'Cxt12E4Bridge'
$watchdogScheduledTaskName = 'Cxt12E4BridgeWatchdog'

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Installed Bridge is missing: $executable"
}
if (-not (Test-Path -LiteralPath $watchdog -PathType Leaf)) {
    throw "Installed Bridge watchdog is missing: $watchdog"
}

$selfTestProcess = Start-Process -FilePath $executable -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
if ($selfTestProcess.ExitCode -ne 0) {
    throw "Installed Bridge self-test failed with exit code $($selfTestProcess.ExitCode)."
}

$actualRunCommand = $null
$shortcutInfo = $null
$watchdogTask = $null
if (-not $SkipAutoStartCheck) {
    $actualRunCommand = (Get-ItemProperty -LiteralPath $runKey -Name $runValueName -ErrorAction Stop).$runValueName
    if ($actualRunCommand -ne $expectedRunCommand) {
        throw "Startup entry mismatch. Expected '$expectedRunCommand', found '$actualRunCommand'."
    }
    Test-StartupApprovedEnabled -SubKey 'Run' -ValueName $runValueName | Out-Null

    if (-not (Test-Path -LiteralPath $startupShortcutPath -PathType Leaf)) {
        throw "Startup shortcut is missing: $startupShortcutPath"
    }
    $shortcutInfo = Get-ShortcutInfo -ShortcutPath $startupShortcutPath
    if ($shortcutInfo.TargetPath -ne $watchdog) {
        throw "Startup shortcut target mismatch. Expected '$watchdog', found '$($shortcutInfo.TargetPath)'."
    }
    if ($shortcutInfo.Arguments -ne $expectedWatchdogArguments) {
        throw "Startup shortcut arguments mismatch. Expected '$expectedWatchdogArguments', found '$($shortcutInfo.Arguments)'."
    }
    Test-StartupApprovedEnabled -SubKey 'StartupFolder' -ValueName $startupShortcutName | Out-Null

    $retiredTask = Get-ScheduledTask -TaskName $retiredScheduledTaskName -ErrorAction SilentlyContinue
    if ($retiredTask) {
        throw "Retired scheduled task '$retiredScheduledTaskName' still exists; run the installer again to remove the direct Bridge Task Scheduler path."
    }

    $watchdogTask = Get-ScheduledTask -TaskName $watchdogScheduledTaskName -ErrorAction Stop
    if ($watchdogTask.Actions.Count -lt 1) {
        throw "Watchdog scheduled task '$watchdogScheduledTaskName' has no action."
    }
    $watchdogAction = $watchdogTask.Actions[0]
    if ($watchdogAction.Execute -ne $watchdog) {
        throw "Watchdog scheduled task executable mismatch. Expected '$watchdog', found '$($watchdogAction.Execute)'."
    }
    if ($watchdogAction.Arguments -ne $expectedWatchdogArguments) {
        throw "Watchdog scheduled task arguments mismatch. Expected '$expectedWatchdogArguments', found '$($watchdogAction.Arguments)'."
    }
}

$residentProcesses = @(
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -eq $executable -and ([string]$_.CommandLine).Contains('--run') }
)
$watchdogProcesses = @(
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -eq $watchdog -and ([string]$_.CommandLine).Contains('--watchdog') }
)
if (-not $SkipProcessCheck -and $residentProcesses.Count -ne 1) {
    throw "Expected exactly one installed Bridge resident process, found $($residentProcesses.Count)."
}
if (-not $SkipProcessCheck -and -not $SkipAutoStartCheck -and $watchdogProcesses.Count -ne 1) {
    throw "Expected exactly one installed Bridge watchdog process, found $($watchdogProcesses.Count)."
}

[pscustomobject]@{
    Verified = $true
    Executable = $executable
    SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $executable).Hash
    WatchdogExecutable = $watchdog
    WatchdogSHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $watchdog).Hash
    AutoStart = $(if ($SkipAutoStartCheck) { 'not checked' } else { $actualRunCommand })
    StartupApprovedRun = $(if ($SkipAutoStartCheck) { 'not checked' } else { 'enabled' })
    StartupShortcut = $(if ($SkipAutoStartCheck) { 'not checked' } else { $startupShortcutPath })
    StartupShortcutApproved = $(if ($SkipAutoStartCheck) { 'not checked' } else { 'enabled' })
    RetiredScheduledTask = $(if ($SkipAutoStartCheck) { 'not checked' } else { 'removed' })
    WatchdogScheduledTask = $(if ($SkipAutoStartCheck) { 'not checked' } else { $watchdogScheduledTaskName })
    ProcessCount = $residentProcesses.Count
    ProcessIds = (@($residentProcesses | Select-Object -ExpandProperty ProcessId) -join ',')
    WatchdogProcessCount = $watchdogProcesses.Count
    WatchdogProcessIds = (@($watchdogProcesses | Select-Object -ExpandProperty ProcessId) -join ',')
    LogPath = (Join-Path $env:LOCALAPPDATA 'Cxt1024Bridge\bridge.log')
} | Format-List
