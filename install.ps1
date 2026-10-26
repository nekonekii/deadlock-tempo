[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$NoAutoStart
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Deadlock Tempo requires Windows 10/11.' }

function Normalize-TempoPath([string]$Value) {
    return [IO.Path]::GetFullPath($Value.Trim('"')).TrimEnd('\')
}
function Get-TempoTask {
    return Get-ScheduledTask -TaskName 'DeadlockTempo Watcher' -ErrorAction SilentlyContinue
}
function Stop-TempoProcesses([string]$Executable) {
    $taskProcesses = @(Get-CimInstance Win32_Process -Filter "Name='DeadlockTempo.exe'" | Where-Object {
        $_.ExecutablePath -and (Normalize-TempoPath $_.ExecutablePath) -eq (Normalize-TempoPath $Executable)
    })
    foreach ($taskInfo in $taskProcesses) {
        $taskProcess = Get-Process -Id $taskInfo.ProcessId -ErrorAction SilentlyContinue
        if ($taskProcess) {
            Stop-Process -InputObject $taskProcess -Force -ErrorAction SilentlyContinue
            if (-not $taskProcess.WaitForExit(5000)) { throw 'Tempo did not exit in time. Retry installation.' }
        }
    }
}

$taskExisting = Get-TempoTask
$taskExistingExe = if ($taskExisting) { [string](@($taskExisting.Actions)[0].Execute) } else { $null }
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    # Reuse this user's installation, including Windows-packaged launcher paths.
    if ($taskExistingExe -and [IO.Path]::GetFileName($taskExistingExe) -eq 'DeadlockTempo.exe') {
        $taskMarker = Join-Path (Split-Path -Parent $taskExistingExe) 'data-directory.txt'
        if (Test-Path -LiteralPath $taskMarker) {
            $taskCandidate = [IO.File]::ReadAllText($taskMarker).Trim()
            if ([IO.Path]::IsPathRooted($taskCandidate) -and
                (Normalize-TempoPath (Join-Path $taskCandidate 'App\DeadlockTempo.exe')) -eq (Normalize-TempoPath $taskExistingExe)) {
                $InstallRoot = $taskCandidate
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        $InstallRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DeadlockTempo'
    }
}
$taskRoot = Normalize-TempoPath $InstallRoot
$taskApp = Join-Path $taskRoot 'App'
$taskExe = Join-Path $taskApp 'DeadlockTempo.exe'
$taskOwnsTask = $taskExistingExe -and (Normalize-TempoPath $taskExistingExe) -eq (Normalize-TempoPath $taskExe)
if ($taskExisting -and -not $taskOwnsTask -and -not $NoAutoStart) {
    throw 'Another Tempo installation owns autostart. Run without -InstallRoot to update it, or use -NoAutoStart for an isolated test.'
}

$taskBuild = Join-Path $PSScriptRoot ('.build\install-' + [Guid]::NewGuid().ToString('N'))
Write-Host '[1/4] Building the application locally...'
& (Join-Path $PSScriptRoot 'outputs\DeadlockTempo\source\build.ps1') -OutputDirectory $taskBuild
$taskBuiltExe = Join-Path $taskBuild 'DeadlockTempo.exe'
Write-Host '[2/4] Checking the clock, schedules and profile migration...'
$taskReport = Join-Path $taskBuild 'tests.txt'
$taskTest = Start-Process -FilePath $taskBuiltExe -ArgumentList @('--test', ('"' + $taskReport + '"')) -WindowStyle Hidden -Wait -PassThru
if ($taskTest.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport)) {
    throw "Validation failed. Report: $taskReport"
}
Write-Host ([IO.File]::ReadAllText($taskReport))

New-Item -ItemType Directory -Path $taskApp -Force | Out-Null
$taskBackup = Join-Path $taskRoot ('backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$taskFiles = @('DeadlockTempo.exe','DeadlockTempo.exe.config','data-directory.txt')
if (Test-Path -LiteralPath $taskExe) {
    New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
    foreach ($taskFile in $taskFiles) {
        $taskOriginal = Join-Path $taskApp $taskFile
        if (Test-Path -LiteralPath $taskOriginal) { Copy-Item -LiteralPath $taskOriginal -Destination (Join-Path $taskBackup $taskFile) }
    }
    foreach ($taskFile in @('profile.json','settings.json')) {
        $taskOriginal = Join-Path $taskRoot $taskFile
        if (Test-Path -LiteralPath $taskOriginal) { Copy-Item -LiteralPath $taskOriginal -Destination (Join-Path $taskBackup $taskFile) }
    }
}
$taskWasEnabled = $taskOwnsTask -and $taskExisting.Settings.Enabled
Write-Host '[3/4] Installing; existing settings and schedules are preserved...'
try {
    if ($taskOwnsTask) {
        Disable-ScheduledTask -TaskName 'DeadlockTempo Watcher' | Out-Null
        Stop-ScheduledTask -TaskName 'DeadlockTempo Watcher'
    }
    Stop-TempoProcesses $taskExe
    Copy-Item -LiteralPath $taskBuiltExe -Destination $taskExe -Force
    Copy-Item -LiteralPath ($taskBuiltExe + '.config') -Destination ($taskExe + '.config') -Force
    [IO.File]::WriteAllText((Join-Path $taskApp 'data-directory.txt'), $taskRoot, (New-Object Text.UTF8Encoding($false)))
    $taskProfile = Join-Path $taskRoot 'profile.json'
    if (-not (Test-Path -LiteralPath $taskProfile)) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'outputs\DeadlockTempo\source\profile.json') -Destination $taskProfile
    }
} catch {
    if (Test-Path -LiteralPath (Join-Path $taskBackup 'DeadlockTempo.exe')) {
        foreach ($taskFile in $taskFiles) {
            $taskSaved = Join-Path $taskBackup $taskFile
            if (Test-Path -LiteralPath $taskSaved) { Copy-Item -LiteralPath $taskSaved -Destination (Join-Path $taskApp $taskFile) -Force }
        }
    }
    throw
} finally {
    if ($taskWasEnabled) {
        Enable-ScheduledTask -TaskName 'DeadlockTempo Watcher' | Out-Null
        Start-ScheduledTask -TaskName 'DeadlockTempo Watcher'
    }
}

if (-not $NoAutoStart) {
    Write-Host '[4/4] Enabling automatic startup with Deadlock...'
    $taskEnable = Start-Process -FilePath $taskExe -ArgumentList '--enable-autostart' -WindowStyle Hidden -Wait -PassThru
    if ($taskEnable.ExitCode -ne 0) { throw 'Autostart registration failed. See autostart.log in the data directory.' }
    # The application resolves Windows filesystem redirection before registering.
    $taskPhysicalRoot = [IO.File]::ReadAllText((Join-Path $taskApp 'data-directory.txt')).Trim()
    $taskPhysicalExe = Join-Path $taskPhysicalRoot 'App\DeadlockTempo.exe'
    $taskRegistered = Get-TempoTask
    if (-not $taskRegistered -or -not $taskRegistered.Settings.Enabled -or
        (Normalize-TempoPath ([string](@($taskRegistered.Actions)[0].Execute))) -ne (Normalize-TempoPath $taskPhysicalExe) -or
        [string](@($taskRegistered.Actions)[0].Arguments) -ne '--watch') {
        throw 'The recovery task was not verified. The application is installed; inspect autostart.log before claiming autostart works.'
    }
    Write-Host 'Autostart verified: DeadlockTempo Watcher. It runs at sign-in, recovers every minute and checks for Deadlock every 2 seconds.'
    Write-Host "Installed: $taskPhysicalExe"
} else {
    Write-Host '[4/4] Autostart registration skipped (-NoAutoStart).'
    Write-Host "Installed: $taskExe"
}
Write-Host 'Control panel: Ctrl+Alt+F10 or the Tempo tray icon. No changes to Steam launch options are needed.'
Write-Host "Validation report: $taskReport"
