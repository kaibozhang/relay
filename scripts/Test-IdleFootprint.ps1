#requires -Version 5.1
<#
.SYNOPSIS
Measures the actual packaged Relay process while its own main window is minimized.
.DESCRIPTION
Parent must first finish its real UIA work and confirm parentReady. Do not launch
before that confirmation. Intended parent-managed background window: 16:07-16:19
(plus five seconds of readiness and setup/cleanup). This script does not schedule
itself or create detached jobs. Parent uses tools.pwsh with run_in_background=true,
tracks that job, and collects it once finished; do not repeatedly poll the soak.
Example command after parentReady:
  & ./scripts/Test-IdleFootprint.ps1 -ParentReady -Minutes 12 -IntervalSeconds 60

Only launch/minimize/terminate the process created here. No other UI operations,
ETW, network capture, clipboard, screenshots, or private-user-directory inspection.
An empty synthetic vault means no cards, reminders, or focus session are seeded.
Only .relay.lock is expected in that vault. Reports live beside it, not inside it.
The native app's one-second timer is left unchanged. Twelve interval samples plus
baseline describe idle resource use, not a battery benchmark or proof of no leaks.
Concurrent disk soak is permitted; shared-host contention can affect observations.
#>
[CmdletBinding()]
param(
    [ValidateRange(1,30)][int]$Minutes = 12,
    [string]$Exe,
    [ValidateRange(10,1800)][int]$IntervalSeconds = 60,
    [switch]$ParentReady
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ParentReady) { throw 'Launch refused: parent must confirm readiness with -ParentReady after its UIA work.' }
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Assert-WorkspacePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Outside workspace: $full"
    }
    if ($full.StartsWith('\') -or $full.Substring(2).Contains(':')) { throw 'UNC/device/alternate-stream paths are not allowed.' }
    $cursor = $full
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing junction/symlink/reparse ancestor: $cursor"
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}
$null = Assert-WorkspacePath $PSScriptRoot
$knownExe = Assert-WorkspacePath (Join-Path $root 'dist/windows/Relay.exe')
if ([string]::IsNullOrWhiteSpace($Exe)) { $Exe = $knownExe }
elseif (-not [IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $root $Exe }
$Exe = Assert-WorkspacePath $Exe
if (-not $Exe.Equals($knownExe, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only the current workspace dist/windows/Relay.exe is allowed, not a copied/test executable.'
}
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Release executable missing: $Exe" }
$run = Assert-WorkspacePath (Join-Path $root ('artifacts/idle-test/' + [guid]::NewGuid().ToString('N')))
$vault = Assert-WorkspacePath (Join-Path $run 'vault')
New-Item -ItemType Directory -Path $vault | Out-Null
$null = Assert-WorkspacePath $vault
$seriesPath = Assert-WorkspacePath (Join-Path $run 'series.json')
$reportPath = Assert-WorkspacePath (Join-Path $run 'report.json')
$utf8 = New-Object Text.UTF8Encoding($false)
function Write-Json([object]$Value, [string]$Path) {
    $safe = Assert-WorkspacePath $Path
    [IO.File]::WriteAllText($safe, (ConvertTo-Json -InputObject $Value -Depth 10), $utf8)
}
$series = New-Object 'System.Collections.Generic.List[object]'
$moduleCaptures = New-Object 'System.Collections.Generic.List[object]'
$ownProcess = $null
$ownPid = $null
$ownHwnd = [IntPtr]::Zero
$watch = New-Object Diagnostics.Stopwatch
$failure = $null
$cleanupError = $null
$processorCount = [Environment]::ProcessorCount
$report = [ordered]@{
    schemaVersion = 1
    status = 'starting'
    startedUtc = [datetime]::UtcNow.ToString('o')
    parentReadyConfirmed = $true
    exe = $Exe
    exeSha256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash
    arguments = @('--vault', $vault)
    fixture = $run
    requestedMinutes = $Minutes
    intervalSeconds = $IntervalSeconds
    readinessSeconds = 5
    processorCount = $processorCount
    scope = 'Owned packaged process only; empty custom vault; main window SW_MINIMIZE (6).'
    caveats = @('Not a battery benchmark.', 'Shared host: concurrent disk soak and other workloads may affect observations.', 'CPU excludes launch/readiness and cleanup; covers the entire baseline-to-final measurement.', 'Module names alone cannot prove absence of an engine in unloaded/static code or child processes.', 'Finite idle observations are not proof of no long-term leaks.')
}
function Capture-Modules([string]$Phase) {
    try {
        $ownProcess.Refresh()
        $names = @($ownProcess.Modules | ForEach-Object { $_.ModuleName } | Sort-Object -Unique)
        $engines = @($names | Where-Object { $_ -match '(?i)webview|electron' })
        $moduleCaptures.Add([pscustomobject]@{
            phase = $Phase; utc = [datetime]::UtcNow.ToString('o'); status = 'captured'
            names = $names; webViewOrElectronNames = $engines
            engineNameObservation = $(if ($engines.Count -eq 0) { 'none observed' } else { 'matching names observed' })
        })
    } catch {
        $moduleCaptures.Add([pscustomobject]@{ phase = $Phase; status = 'unavailable'; error = $_.Exception.Message })
    }
}
function Capture-Sample([int]$Index) {
    $ownProcess.Refresh()
    if ($ownProcess.HasExited) { throw 'Owned app exited during measurement.' }
    $cpu = $ownProcess.TotalProcessorTime.TotalSeconds
    $elapsed = $watch.Elapsed.TotalSeconds
    $working = $ownProcess.WorkingSet64
    $private = $ownProcess.PrivateMemorySize64
    $handles = $ownProcess.HandleCount
    $threads = $ownProcess.Threads.Count
    $intervalCpu = $null; $intervalElapsed = $null; $oneCore = $null; $normalized = $null
    if ($series.Count -gt 0) {
        $previous = $series[$series.Count - 1]
        $intervalCpu = $cpu - $previous.totalProcessorTimeSeconds
        $intervalElapsed = $elapsed - $previous.elapsedSeconds
        if ($intervalElapsed -gt 0) {
            $oneCore = 100.0 * $intervalCpu / $intervalElapsed
            $normalized = $oneCore / $processorCount
        }
    }
    $series.Add([pscustomobject]@{
        index = $Index; utc = [datetime]::UtcNow.ToString('o'); elapsedSeconds = $elapsed
        totalProcessorTimeSeconds = $cpu; workingSetBytes = $working; privateMemoryBytes = $private
        handleCount = $handles; threadCount = $threads
        intervalCpuSeconds = $intervalCpu; intervalElapsedSeconds = $intervalElapsed
        intervalOneCorePercent = $oneCore; intervalNormalizedPercent = $normalized
    })
    Write-Json -Value $series.ToArray() -Path $seriesPath
}
try {
    # PowerShell 5 may use temporary compiler files: confine those to our fixture.
    $compilerTemp = Assert-WorkspacePath (Join-Path $run 'compiler-temp')
    New-Item -ItemType Directory -Path $compilerTemp | Out-Null
    $oldTemp = $env:TEMP; $oldTmp = $env:TMP
    try {
        $env:TEMP = $compilerTemp; $env:TMP = $compilerTemp
        if (-not ('RelayIdleFootprint.Native' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace RelayIdleFootprint {
    public static class Native {
        [DllImport("user32.dll", EntryPoint="ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool NativeShowWindow(IntPtr ownHwnd, int command);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hwnd);
    }
}
'@
        }
    } finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp }
    $null = Assert-WorkspacePath $Exe
    $null = Assert-WorkspacePath $vault
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $run
    # Generated vault path cannot contain quotes and has no trailing backslash.
    $start.Arguments = '--vault "' + $vault + '"'
    $ownProcess = New-Object Diagnostics.Process
    $ownProcess.StartInfo = $start
    if (-not $ownProcess.Start()) { throw 'Owned process failed to start.' }
    $ownPid = $ownProcess.Id
    # Fixed readiness wait, not a tool polling loop. Fail closed if no main HWND.
    Start-Sleep -Seconds 5
    $ownProcess.Refresh()
    if ($ownProcess.HasExited) { throw 'App exited before readiness.' }
    $ownHwnd = $ownProcess.MainWindowHandle
    [uint32]$windowPid = 0
    if ($ownHwnd -eq [IntPtr]::Zero) { throw 'No main window after five-second readiness wait.' }
    $null = [RelayIdleFootprint.Native]::GetWindowThreadProcessId($ownHwnd, [ref]$windowPid)
    if ($windowPid -ne $ownPid) { throw 'Refusing to minimize a window not owned by the launched process.' }
    $SW_MINIMIZE = 6
    $null = [RelayIdleFootprint.Native]::NativeShowWindow($ownHwnd, $SW_MINIMIZE)
    if (-not [RelayIdleFootprint.Native]::IsIconic($ownHwnd)) { throw 'Owned main window did not minimize.' }
    Capture-Modules 'before-measurement'
    $report.status = 'measuring'
    $report.measurementStartedUtc = [datetime]::UtcNow.ToString('o')
    $watch.Start()
    Capture-Sample 0
    $duration = $Minutes * 60.0
    $deadline = $series[0].elapsedSeconds + $duration
    $target = [Math]::Min($series[0].elapsedSeconds + $IntervalSeconds, $deadline)
    $index = 1
    while ($true) {
        $remainingMs = [Math]::Ceiling(($target - $watch.Elapsed.TotalSeconds) * 1000)
        if ($remainingMs -gt 0) { Start-Sleep -Milliseconds ([int]$remainingMs) }
        Capture-Sample $index
        if ($series[$series.Count - 1].elapsedSeconds -ge $deadline) { break }
        $index++
        $target = [Math]::Min($target + $IntervalSeconds, $deadline)
    }
    $watch.Stop()
    Capture-Modules 'after-measurement'
    $report.status = 'completed'
} catch {
    $failure = $_.Exception.Message
    $report.status = 'failed'
} finally {
    $watch.Stop()
    # Retain the Process object/handle we created: no process-name or PID lookup.
    if ($null -ne $ownProcess) {
        try {
            if (-not $ownProcess.HasExited) {
                $ownProcess.Kill()
                if (-not $ownProcess.WaitForExit(5000)) { throw 'Owned process did not exit within cleanup deadline.' }
            }
        } catch { $cleanupError = $_.Exception.Message; $report.status = 'failed' }
        finally { $ownProcess.Dispose() }
    }
    $report.ownedProcessId = $ownPid
    $report.ownedMainHwnd = $ownHwnd.ToInt64()
    $report.failure = $failure
    $report.cleanupError = $cleanupError
    $report.finishedUtc = [datetime]::UtcNow.ToString('o')
    $report.sampleCountIncludingBaseline = $series.Count
    $report.intervalSampleCount = [Math]::Max(0, $series.Count - 1)
    $report.moduleCaptures = $moduleCaptures.ToArray()
    if ($series.Count -ge 2) {
        $first = $series[0]; $last = $series[$series.Count - 1]
        $elapsed = $last.elapsedSeconds - $first.elapsedSeconds
        $cpuSeconds = $last.totalProcessorTimeSeconds - $first.totalProcessorTimeSeconds
        $report.measurementElapsedSeconds = $elapsed
        $report.measurementCpuSeconds = $cpuSeconds
        $report.oneCorePercent = 100.0 * $cpuSeconds / $elapsed
        $report.normalizedPercent = 100.0 * $cpuSeconds / $elapsed / $processorCount
        $report.normalizedFormula = '100 * cpuSeconds / elapsedSeconds / ProcessorCount'
        $report.endMinusBaseline = [ordered]@{
            workingSetBytes = $last.workingSetBytes - $first.workingSetBytes
            privateMemoryBytes = $last.privateMemoryBytes - $first.privateMemoryBytes
            handles = $last.handleCount - $first.handleCount
            threads = $last.threadCount - $first.threadCount
        }
    }
    $null = Assert-WorkspacePath $vault
    $vaultNames = @(Get-ChildItem -LiteralPath $vault -Force | Select-Object -ExpandProperty Name)
    $report.vaultEntryNamesAfterCleanup = $vaultNames
    $unexpected = @($vaultNames | Where-Object { $_ -ne '.relay.lock' })
    $report.onlyExpectedLockPresent = ($unexpected.Count -eq 0 -and $vaultNames -contains '.relay.lock')
    if (-not $report.onlyExpectedLockPresent) { $report.status = 'failed' }
    Write-Json -Value $series.ToArray() -Path $seriesPath
    Write-Json -Value $report -Path $reportPath
    Write-Output $reportPath
}
if ($report.status -ne 'completed') { throw "Idle measurement failed; inspect $reportPath" }
