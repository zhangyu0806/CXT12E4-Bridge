[CmdletBinding()]
param(
    [string]$InstallDirectory = '',
    [switch]$NoAutoStart,
    [switch]$NoStart,
    [switch]$PrepareOnly,
    [switch]$InstallWslIntegrations
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Get-SourceBuildIdentity {
    # This is provenance metadata only. A baseline commit never asserts that
    # the current working tree is clean or replaces the native build/self-test.
    $unknown = [pscustomobject]@{ Commit = $null; Source = 'unavailable' }
    if (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git')) {
        $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue
        if ($git) {
            $process = New-Object Diagnostics.Process
            try {
                $process.StartInfo.FileName = $git.Source
                $process.StartInfo.Arguments = 'rev-parse --verify HEAD'
                $process.StartInfo.WorkingDirectory = $repositoryRoot
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.CreateNoWindow = $true
                $process.StartInfo.RedirectStandardOutput = $true
                $process.StartInfo.RedirectStandardError = $true
                [void]$process.Start()
                $commit = $process.StandardOutput.ReadToEnd().Trim()
                [void]$process.StandardError.ReadToEnd()
                $process.WaitForExit()
                if ($process.ExitCode -eq 0 -and $commit -match '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') {
                    return [pscustomobject]@{ Commit = $commit; Source = 'repository HEAD; working-tree changes may be present' }
                }
            }
            catch { Write-Verbose "Git build identity unavailable: $($_.Exception.Message)" }
            finally { $process.Dispose() }
        }
        return $unknown
    }

    # ZIP installations have no .git. Read package metadata only after its
    # payload hashes match, without accidentally discovering a parent checkout.
    foreach ($name in @('package-manifest.json', 'build-manifest.json')) {
        $path = Join-Path $repositoryRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        try {
            if ((Get-Item -LiteralPath $path).Length -gt 1048576) { continue }
            $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $key = if ($name -eq 'package-manifest.json') { 'sourceCommit' } else { 'commit' }
            $property = $document.PSObject.Properties[$key]
            if ($null -eq $property -or [string]$property.Value -notmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') { continue }
            $commit = [string]$property.Value
            if ($name -eq 'package-manifest.json') {
                $hashProperty = $document.PSObject.Properties['files']
                if ($null -eq $hashProperty) { continue }
                $entries = @($hashProperty.Value.PSObject.Properties)
                if ($entries.Count -eq 0 -or $entries.Count -gt 2000) { continue }
                $rootPrefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                foreach ($entry in $entries) {
                    $candidate = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $entry.Name))
                    if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                        [string]$entry.Value -notmatch '^[0-9a-fA-F]{64}$' -or
                        -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
                        (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne [string]$entry.Value) {
                        throw 'Package payload hash mismatch; build identity is unavailable.'
                    }
                }
            }
            else {
                $hashProperty = $document.PSObject.Properties['executableSha256']
                $binary = Join-Path $repositoryRoot 'outputs\Cxt12E4Bridge.exe'
                if ($null -eq $hashProperty -or [string]$hashProperty.Value -notmatch '^[0-9a-fA-F]{64}$' -or
                    -not (Test-Path -LiteralPath $binary -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne [string]$hashProperty.Value) { continue }
            }
            return [pscustomobject]@{ Commit = $commit; Source = "$name verified payload; build baseline only, not a clean-tree guarantee" }
        }
        catch { Write-Verbose "Package build identity unavailable: $($_.Exception.Message)" }
    }
    return $unknown
}

function Get-BridgeProcesses {
    @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -eq 'Cxt12E4Bridge.exe' -or
                $_.Name -eq 'Cxt1024Bridge.exe'
            }
    )
}

function Get-BridgeWatchdogProcesses {
    param([string]$WatchdogExecutable = '')

    @(
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -eq 'Cxt12E4Watchdog.exe' -and
                ([string]::IsNullOrWhiteSpace($WatchdogExecutable) -or $_.ExecutablePath -eq $WatchdogExecutable)
            }
    )
}

function Wait-ForProcessExit {
    param(
        [scriptblock]$Query,
        [string]$Name,
        [int]$TimeoutMilliseconds = 10000
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $processes = @(& $Query)
        if ($processes.Count -eq 0) {
            $timer.Stop()
            return
        }
        Start-Sleep -Milliseconds 100
    } while ($timer.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    $timer.Stop()

    $remaining = @(& $Query)
    throw "$Name did not exit within $TimeoutMilliseconds ms. Remaining PID(s): $($remaining.ProcessId -join ', ')"
}

function Unregister-BridgeScheduledTask {
    param([string]$TaskName)

    if (Get-Command Unregister-ScheduledTask -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

function Get-BridgeWatchdogCommand {
    param(
        [string]$WatchdogExecutable,
        [string]$BridgeExecutable
    )

    '"{0}" --watchdog "{1}"' -f $WatchdogExecutable, $BridgeExecutable
}
function Get-WatchdogArgumentString {
    param([string]$BridgeExecutable)

    '--watchdog "{0}"' -f $BridgeExecutable
}

function Get-StartupApprovedBytes {
    [byte[]](0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
}

function Set-StartupApprovedEnabled {
    param(
        [ValidateSet('Run', 'StartupFolder')]
        [string]$SubKey,
        [string]$ValueName
    )

    $path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$SubKey"
    New-Item -Path $path -Force | Out-Null
    New-ItemProperty -LiteralPath $path -Name $ValueName -Value (Get-StartupApprovedBytes) -PropertyType Binary -Force | Out-Null
}

function Remove-StartupApprovedEntry {
    param(
        [ValidateSet('Run', 'StartupFolder')]
        [string]$SubKey,
        [string]$ValueName
    )

    $path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\$SubKey"
    Remove-ItemProperty -LiteralPath $path -Name $ValueName -ErrorAction SilentlyContinue
}

function Set-BridgeStartupShortcut {
    param(
        [string]$ShortcutPath,
        [string]$WatchdogExecutable,
        [string]$BridgeExecutable
    )

    $startupDirectory = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Force -Path $startupDirectory | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $WatchdogExecutable
    $shortcut.Arguments = Get-WatchdogArgumentString -BridgeExecutable $BridgeExecutable
    $shortcut.WorkingDirectory = Split-Path -Parent $WatchdogExecutable
    $shortcut.Description = 'CXT 12E4 Bridge watchdog'
    $shortcut.WindowStyle = 7
    $shortcut.Save()
}

function Register-BridgeWatchdogScheduledTask {
    param(
        [string]$TaskName,
        [string]$WatchdogExecutable,
        [string]$BridgeExecutable
    )

    foreach ($command in @('New-ScheduledTaskAction', 'New-ScheduledTaskTrigger', 'New-ScheduledTaskSettingsSet', 'New-ScheduledTaskPrincipal', 'Register-ScheduledTask')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "ScheduledTasks command is unavailable: $command"
        }
    }

    $action = New-ScheduledTaskAction -Execute $WatchdogExecutable -Argument (Get-WatchdogArgumentString -BridgeExecutable $BridgeExecutable) -WorkingDirectory (Split-Path -Parent $WatchdogExecutable)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    try {
        $trigger.Delay = 'PT45S'
    }
    catch {
        Write-Warning "Unable to set scheduled task startup delay: $($_.Exception.Message)"
    }

    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Seconds 0)
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description 'Starts the CXT 12E4 Bridge watchdog at logon.' -Force | Out-Null
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $repositoryRoot 'outputs\home-runtime'
}

$buildScript = Join-Path $repositoryRoot 'build.ps1'
$builtExecutable = Join-Path $repositoryRoot 'outputs\Cxt12E4Bridge.exe'
$guideSource = Join-Path $repositoryRoot 'README-CXT12E4.md'
$targetExecutable = Join-Path $InstallDirectory 'Cxt12E4Bridge.exe'
$targetWatchdogExecutable = Join-Path $InstallDirectory 'Cxt12E4Watchdog.exe'
$targetGuide = Join-Path $InstallDirectory 'README-CXT12E4.md'
$legacyCmdLauncher = Join-Path $InstallDirectory 'Start-Cxt12E4Bridge.cmd'
$legacyPowerShellLauncher = Join-Path $InstallDirectory 'Start-Cxt12E4Bridge.ps1'
$manifestPath = Join-Path $InstallDirectory 'install-manifest.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'Cxt12E4Bridge'
$startupFolder = [Environment]::GetFolderPath('Startup')
$startupShortcutName = 'Cxt12E4BridgeWatchdog.lnk'
$startupShortcutPath = Join-Path $startupFolder $startupShortcutName
$retiredScheduledTaskName = 'Cxt12E4Bridge'
$watchdogScheduledTaskName = 'Cxt12E4BridgeWatchdog'

foreach ($required in @($buildScript, $guideSource)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required repository file is missing: $required"
    }
}

$sourceBuildIdentity = Get-SourceBuildIdentity

Write-Host 'Building console Bridge for self-test...'
& $buildScript
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $builtExecutable -PathType Leaf)) {
    throw "Console Bridge build failed with exit code $LASTEXITCODE."
}

& $builtExecutable --self-test
if ($LASTEXITCODE -ne 0) {
    throw "Bridge self-test failed with exit code $LASTEXITCODE; installation was not changed."
}

Write-Host 'Building the windowed Bridge payload...'
& $buildScript -Windowed
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $builtExecutable -PathType Leaf)) {
    throw "Windowed Bridge build failed with exit code $LASTEXITCODE."
}

$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $builtExecutable).Hash
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null

if (-not $PrepareOnly) {
    $watchdogExitProcess = Start-Process -FilePath $builtExecutable -ArgumentList '--watchdog-exit' -Wait -PassThru -WindowStyle Hidden
    $watchdogExitCode = $watchdogExitProcess.ExitCode
    if ($watchdogExitCode -ne 0 -and $watchdogExitCode -ne 3) {
        throw "Unable to signal the running Bridge watchdog (exit code $watchdogExitCode)."
    }
    Wait-ForProcessExit -Query { Get-BridgeWatchdogProcesses } -Name 'Bridge watchdog'

    # The exit event is shared by old and new filenames, so this also stops a
    # legacy Dropbox/startup copy without guessing its path or killing it.
    $exitProcess = Start-Process -FilePath $builtExecutable -ArgumentList '--exit' -Wait -PassThru -WindowStyle Hidden
    $exitCode = $exitProcess.ExitCode
    if ($exitCode -ne 0 -and $exitCode -ne 3) {
        throw "Unable to signal the running Bridge (exit code $exitCode)."
    }
    Wait-ForProcessExit -Query { Get-BridgeProcesses } -Name 'Bridge'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backupPath = $null
if (Test-Path -LiteralPath $targetExecutable -PathType Leaf) {
    $backupPath = Join-Path $InstallDirectory "Cxt12E4Bridge.backup-$stamp.exe"
    Copy-Item -LiteralPath $targetExecutable -Destination $backupPath
}

$watchdogBackupPath = $null
if (Test-Path -LiteralPath $targetWatchdogExecutable -PathType Leaf) {
    $watchdogBackupPath = Join-Path $InstallDirectory "Cxt12E4Watchdog.backup-$stamp.exe"
    Copy-Item -LiteralPath $targetWatchdogExecutable -Destination $watchdogBackupPath
}

$temporaryExecutable = Join-Path $InstallDirectory "Cxt12E4Bridge.installing-$stamp.exe"
$temporaryWatchdogExecutable = Join-Path $InstallDirectory "Cxt12E4Watchdog.installing-$stamp.exe"
Copy-Item -LiteralPath $builtExecutable -Destination $temporaryExecutable
Copy-Item -LiteralPath $builtExecutable -Destination $temporaryWatchdogExecutable
$temporaryHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporaryExecutable).Hash
$temporaryWatchdogHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporaryWatchdogExecutable).Hash
if ($temporaryHash -ne $sourceHash) {
    throw "Copied payload hash mismatch: source=$sourceHash copied=$temporaryHash"
}
if ($temporaryWatchdogHash -ne $sourceHash) {
    throw "Copied watchdog hash mismatch: source=$sourceHash copied=$temporaryWatchdogHash"
}
Move-Item -LiteralPath $temporaryExecutable -Destination $targetExecutable -Force
Move-Item -LiteralPath $temporaryWatchdogExecutable -Destination $targetWatchdogExecutable -Force
Copy-Item -LiteralPath $guideSource -Destination $targetGuide -Force
foreach ($legacy in @($legacyCmdLauncher, $legacyPowerShellLauncher)) {
    if (Test-Path -LiteralPath $legacy -PathType Leaf) {
        Remove-Item -LiteralPath $legacy -Force
    }
}

$installedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $targetExecutable).Hash
$watchdogHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $targetWatchdogExecutable).Hash
if ($installedHash -ne $sourceHash) {
    throw "Installed payload hash mismatch: source=$sourceHash installed=$installedHash"
}
if ($watchdogHash -ne $sourceHash) {
    throw "Installed watchdog hash mismatch: source=$sourceHash installed=$watchdogHash"
}

if (-not $PrepareOnly) {
    if ($NoAutoStart) {
        Remove-ItemProperty -LiteralPath $runKey -Name $runValueName -ErrorAction SilentlyContinue
        Remove-StartupApprovedEntry -SubKey 'Run' -ValueName $runValueName
        if (Test-Path -LiteralPath $startupShortcutPath -PathType Leaf) {
            Remove-Item -LiteralPath $startupShortcutPath -Force
        }
        Remove-StartupApprovedEntry -SubKey 'StartupFolder' -ValueName $startupShortcutName
        Unregister-BridgeScheduledTask -TaskName $retiredScheduledTaskName
        Unregister-BridgeScheduledTask -TaskName $watchdogScheduledTaskName
    }
    else {
        New-Item -Path $runKey -Force | Out-Null
        $runCommand = Get-BridgeWatchdogCommand -WatchdogExecutable $targetWatchdogExecutable -BridgeExecutable $targetExecutable
        New-ItemProperty -LiteralPath $runKey -Name $runValueName -Value $runCommand -PropertyType String -Force | Out-Null
        Set-StartupApprovedEnabled -SubKey 'Run' -ValueName $runValueName
        Set-BridgeStartupShortcut -ShortcutPath $startupShortcutPath -WatchdogExecutable $targetWatchdogExecutable -BridgeExecutable $targetExecutable
        Set-StartupApprovedEnabled -SubKey 'StartupFolder' -ValueName $startupShortcutName
        Unregister-BridgeScheduledTask -TaskName $retiredScheduledTaskName
        Register-BridgeWatchdogScheduledTask -TaskName $watchdogScheduledTaskName -WatchdogExecutable $targetWatchdogExecutable -BridgeExecutable $targetExecutable
    }

    # Start and verify the core runtime before optional WSL work. If a later
    # optional integration fails, the keyboard remains usable.
    if (-not $NoStart) {
        Start-Process -FilePath $targetExecutable -ArgumentList '--run' -WindowStyle Hidden
        Start-Sleep -Seconds 2
        $exactProcesses = @(
            Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                Where-Object { $_.ExecutablePath -eq $targetExecutable -and ([string]$_.CommandLine).Contains('--run') }
        )
        if ($exactProcesses.Count -ne 1) {
            throw "Expected exactly one installed Bridge process, found $($exactProcesses.Count)."
        }

        if (-not $NoAutoStart) {
            Start-Process -FilePath $targetWatchdogExecutable -ArgumentList @('--watchdog', "`"$targetExecutable`"") -WindowStyle Hidden
            Start-Sleep -Seconds 2
            $watchdogProcesses = @(Get-BridgeWatchdogProcesses -WatchdogExecutable $targetWatchdogExecutable)
            if ($watchdogProcesses.Count -ne 1) {
                throw "Expected exactly one installed Bridge watchdog process, found $($watchdogProcesses.Count)."
            }
        }
    }

    if ($InstallWslIntegrations) {
        $wslInstallerWindows = Join-Path $repositoryRoot 'install\wsl-home\Install-Cxt12E4Integrations.sh'
        if (-not (Test-Path -LiteralPath $wslInstallerWindows -PathType Leaf)) {
            throw "WSL installer is missing: $wslInstallerWindows"
        }
        $wslInstallerLinux = (& wsl.exe --exec wslpath -a $wslInstallerWindows).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslInstallerLinux)) {
            throw 'Unable to convert the WSL installer path. Is the target WSL distribution running?'
        }
        & wsl.exe --exec bash $wslInstallerLinux
        if ($LASTEXITCODE -ne 0) {
            throw "WSL integration installation failed with exit code $LASTEXITCODE."
        }
    }
}

$manifest = [ordered]@{
    installedAt = (Get-Date).ToString('o')
    repositoryCommit = $sourceBuildIdentity.Commit
    repositoryCommitSource = $sourceBuildIdentity.Source
    executable = $targetExecutable
    sha256 = $installedHash
    watchdogExecutable = $targetWatchdogExecutable
    watchdogSha256 = $watchdogHash
    backup = $backupPath
    watchdogBackup = $watchdogBackupPath
    autoStartCommand = $(if (-not $NoAutoStart -and -not $PrepareOnly) { Get-BridgeWatchdogCommand -WatchdogExecutable $targetWatchdogExecutable -BridgeExecutable $targetExecutable } else { $null })
    autoStart = (-not $NoAutoStart -and -not $PrepareOnly)
    startupApprovedRun = (-not $NoAutoStart -and -not $PrepareOnly)
    startupShortcut = $(if (-not $NoAutoStart -and -not $PrepareOnly) { $startupShortcutPath } else { $null })
    startupShortcutApproved = (-not $NoAutoStart -and -not $PrepareOnly)
    retiredScheduledTask = $false
    watchdogScheduledTask = $(if (-not $NoAutoStart -and -not $PrepareOnly) { $watchdogScheduledTaskName } else { $null })
    scheduledTask = (-not $NoAutoStart -and -not $PrepareOnly)
    started = (-not $NoStart -and -not $PrepareOnly)
    watchdogStarted = (-not $NoStart -and -not $NoAutoStart -and -not $PrepareOnly)
    wslIntegrations = ($InstallWslIntegrations -and -not $PrepareOnly)
    prepareOnly = [bool]$PrepareOnly
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8

[pscustomobject]@{
    Mode = $(if ($PrepareOnly) { 'PrepareOnly' } else { 'Installed' })
    InstallDirectory = $InstallDirectory
    Executable = $targetExecutable
    SHA256 = $installedHash
    WatchdogExecutable = $targetWatchdogExecutable
    WatchdogSHA256 = $watchdogHash
    Backup = $backupPath
    WatchdogBackup = $watchdogBackupPath
    AutoStart = (-not $NoAutoStart -and -not $PrepareOnly)
    StartupApprovedRun = (-not $NoAutoStart -and -not $PrepareOnly)
    StartupShortcut = $(if (-not $NoAutoStart -and -not $PrepareOnly) { $startupShortcutPath } else { $null })
    StartupShortcutApproved = (-not $NoAutoStart -and -not $PrepareOnly)
    RetiredScheduledTask = $false
    WatchdogScheduledTask = $(if (-not $NoAutoStart -and -not $PrepareOnly) { $watchdogScheduledTaskName } else { $null })
    ScheduledTask = (-not $NoAutoStart -and -not $PrepareOnly)
    Started = (-not $NoStart -and -not $PrepareOnly)
    WatchdogStarted = (-not $NoStart -and -not $NoAutoStart -and -not $PrepareOnly)
    WslIntegrations = ($InstallWslIntegrations -and -not $PrepareOnly)
} | Format-List
