#requires -Version 7.6
<# Validates the clean ZIP and published files, then launches only its own extracted
copy with an explicit synthetic vault. No desktop screenshots or user vault reads.
PowerShell 7.6 (.NET 10) is needed for this tester's UIAutomation assemblies;
end users need neither PowerShell nor an installed .NET runtime to run Relay. #>
[CmdletBinding()]
param([switch]$SkipLaunch, [ValidateRange(5, 120)][int]$StartupTimeoutSeconds = 45)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$runDir = Join-Path $root "artifacts/release-test/$runId"
$reportPath = Join-Path $runDir 'report.json'
$owned = $null
$exitCode = 1
$report = [ordered]@{ schemaVersion = 1; startedUtc = [datetime]::UtcNow.ToString('o'); status = 'failed';
    checks = [Collections.Generic.List[object]]::new(); launch = 'not-run'; cleanVmVerified = $false;
    limitations = @('Not a clean-VM test. PATH/DOTNET_ROOT isolation and local coreclr loading are evidence, not a clean-machine guarantee.',
        'No registry, firewall, Shell integration or SmartScreen bypass is attempted. No desktop capture is taken.'); error = $null }
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Pass([string]$Name, [object]$Detail) { $report.checks.Add([ordered]@{ name = $Name; passed = $true; detail = $Detail }) }
function Assert-WorkspacePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    Require ($full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Path is outside the workspace.'
    $cursor = $full
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            Require (-not ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) "Refusing reparse point: $cursor"
        }
        if ($cursor -eq $root) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
        Require (-not [string]::IsNullOrEmpty($cursor)) 'Workspace boundary validation failed.'
    }
    return $full
}
function Test-Forbidden([string]$Name) {
    return $Name -match '(?i)(^|/)(Data|drafts|[.]git|tests?|artifacts|diagnostics?|node_modules|localStorage)(/|$)' -or
        $Name -match '(?i)([.]pdb|[.]log|[.]dmp|[.]etl|[.]png|[.]jpe?g|[.]html?|[.]m?js|[.]css|[.]map)$' -or
        $Name -match '(?i)(^|/)(createdump[.]exe|vault[.]json.*|focus[.]json.*|[.]relay[.]lock|.*(?:testhost|WebView2|msedge|chromium|playwright).*)$'
}
function Assert-RelativeName([string]$Name) {
    Require (-not [string]::IsNullOrWhiteSpace($Name)) 'Empty package path.'
    Require (-not [IO.Path]::IsPathRooted($Name)) 'Absolute package path.'
    Require (-not ($Name.Contains(':') -or $Name.Contains([char]92))) 'Noncanonical package path.'
    Require (-not (@($Name.Split('/') | Where-Object { $_ -in @('..', '.', '') }).Count)) 'Traversal or empty package segment.'
    Require (-not (Test-Forbidden $Name)) "Forbidden package content: $Name"
}
function Assert-Pe([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        Require ($stream.Length -ge 128) 'Executable is too small.'
        Require ($reader.ReadUInt16() -eq 0x5A4D) 'Executable lacks MZ magic.'
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        Require ($offset -ge 64 -and $offset -le $stream.Length - 26) 'Invalid PE header offset.'
        $stream.Position = $offset
        Require ($reader.ReadUInt32() -eq 0x00004550) 'Executable lacks PE signature.'
        Require ($reader.ReadUInt16() -eq 0x8664) 'Executable is not AMD64/x64.'
        $stream.Position = $offset + 24
        Require ($reader.ReadUInt16() -eq 0x20B) 'Executable is not PE32+.'
    } finally { $reader.Dispose(); $stream.Dispose() }
}

try {
    Require $IsWindows 'Release verification requires Windows.'
    $null = Assert-WorkspacePath $runDir
    $null = New-Item -ItemType Directory -Path $runDir
    $zipPath = Assert-WorkspacePath (Join-Path $root 'dist/Relay-Windows-x64.zip')
    $hashPath = Assert-WorkspacePath (Join-Path $root 'dist/Relay-Windows-x64.zip.sha256')
    $published = Assert-WorkspacePath (Join-Path $root 'dist/windows')
    $expectedHash = ((Get-Content -LiteralPath $hashPath -Raw).Trim() -split ' +')[0]
    Require ($expectedHash -match '^[a-fA-F0-9]{64}$') 'Invalid SHA256 sidecar.'
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Require ($actualHash -eq $expectedHash) 'ZIP SHA256 mismatch.'
    $report.zipSha256 = $actualHash
    Pass 'zip-sha256' $actualHash
    $package = Assert-WorkspacePath (Join-Path $runDir 'package')
    $null = New-Item -ItemType Directory -Path $package
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        Require ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 5000) 'Unexpected ZIP entry count.'
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            Assert-RelativeName $name
            Require ($seen.Add($name)) "Duplicate ZIP entry: $name"
            $total += $entry.Length
            Require ($entry.Length -le 536870912 -and $total -le 1073741824) 'Unreasonable uncompressed ZIP size.'
            $destination = Assert-WorkspacePath (Join-Path $package $name)
            Require ($destination.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'ZIP extraction escaped its destination.'
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
        }
    } finally { $archive.Dispose() }
    Pass 'clean-archive' ([ordered]@{ files = $seen.Count; uncompressedBytes = $total })
    $manifest = Get-Content -LiteralPath (Join-Path $package 'release-manifest.json') -Raw | ConvertFrom-Json
    Require ($manifest.schemaVersion -eq 1 -and $manifest.application -eq 'Relay') 'Unrecognized release manifest.'
    Require ($manifest.selfContained -and -not $manifest.singleFile -and $manifest.runtime -eq 'win-x64') 'Incorrect publish configuration.'
    $manifestNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        Assert-RelativeName $entry.name
        Require ($manifestNames.Add($entry.name)) "Duplicate manifest file: $($entry.name)"
        Require ($entry.sha256 -match '^[a-fA-F0-9]{64}$' -and $entry.size -ge 0) 'Invalid manifest size/hash.'
        foreach ($base in @($package, $published)) {
            $file = Assert-WorkspacePath (Join-Path $base $entry.name)
            Require ((Get-Item -LiteralPath $file).Length -eq $entry.size) "File size mismatch: $($entry.name)"
            Require ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $entry.sha256) "File hash mismatch: $($entry.name)"
        }
    }
    Require ($seen.Count -eq $manifestNames.Count + 1) 'Archive has unlisted files.'
    foreach ($name in $seen) { Require ($name -eq 'release-manifest.json' -or $manifestNames.Contains($name)) "Unlisted file: $name" }
    foreach ($copy in @((Join-Path $published 'release-manifest.json'), (Join-Path $root 'dist/Relay-Windows-x64.manifest.json'))) {
        $null = Assert-WorkspacePath $copy
        Require ((Get-FileHash -LiteralPath $copy).Hash -eq (Get-FileHash -LiteralPath (Join-Path $package 'release-manifest.json')).Hash) 'Manifest copies differ.'
    }
    Pass 'manifest-integrity' $manifestNames.Count
    $required = @('Relay.exe', 'Relay.dll', 'Relay.Core.dll', 'Relay.deps.json', 'Relay.runtimeconfig.json',
        'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll',
        'PresentationCore.dll', 'WindowsBase.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll', '使用说明.md', '开始使用.txt', 'LICENSE', 'NOTICE')
    foreach ($name in $required) { Require ($manifestNames.Contains($name)) "Required release file missing: $name" }
    Require ((Test-Path -LiteralPath (Join-Path $package 'portable.txt')) -eq [bool]$manifest.portable) 'Portable mode does not match manifest.'
    $runtime = (Get-Content -LiteralPath (Join-Path $package 'Relay.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
    Require ($runtime.tfm -eq 'net10.0') 'Runtime is not .NET 10.'
    Require ($null -eq $runtime.PSObject.Properties['framework'] -and $null -eq $runtime.PSObject.Properties['frameworks']) 'Framework-dependent runtimeconfig found.'
    Require ($null -ne $runtime.PSObject.Properties['includedFrameworks']) 'Self-contained includedFrameworks absent.'
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $framework = @($runtime.includedFrameworks | Where-Object { $_.name -eq $name -and $_.version -match '^10[.]' })
        Require ($framework.Count -eq 1) "Bundled .NET 10 framework missing: $name"
    }
    foreach ($framework in $runtime.includedFrameworks) {
        $legalNames = if ($framework.name -eq 'Microsoft.NETCore.App') { @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') }
            elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') { @('LICENSE') } else { @() }
        foreach ($legal in $legalNames) {
            $relative = "licenses/$($framework.name)/$($framework.version)/$legal"
            Require ($manifestNames.Contains($relative)) "Missing matching runtime legal file: $relative"
            Require ((Get-Item -LiteralPath (Join-Path $package $relative)).Length -gt 0) 'Empty runtime legal file.'
        }
    }
    Pass 'runtime-legal-notices' @($manifest.runtimeLegal)
    $exe = Join-Path $package 'Relay.exe'
    Assert-Pe $exe
    Add-Type -AssemblyName System.Drawing.Common
    $icon = [Drawing.Icon]::ExtractAssociatedIcon($exe)
    try { Require ($null -ne $icon -and $icon.Width -gt 0) 'Executable icon could not be extracted.' } finally { if ($null -ne $icon) { $icon.Dispose() } }
    $signature = (Get-AuthenticodeSignature -LiteralPath $exe).Status.ToString()
    Require ($signature -eq $manifest.signingStatus) 'Signature status differs from manifest.'
    $report.signingStatus = $signature
    Pass 'native-self-contained-x64' ([ordered]@{ signingStatus = $signature; iconExtracted = $true; portable = $manifest.portable })

    if ($SkipLaunch) {
        $report.status = 'static-only'
        $report.launch = 'skipped-explicitly'
    } else {
        # These are TEST process variables only. Never inspect the actual user profile.
        $vault = Assert-WorkspacePath (Join-Path $runDir 'vault')
        $localAppData = Assert-WorkspacePath (Join-Path $runDir 'isolated-localappdata')
        $missingRuntime = Assert-WorkspacePath (Join-Path $runDir 'no-installed-dotnet')
        $null = New-Item -ItemType Directory -Path $localAppData, $missingRuntime
        $defaultPortable = Join-Path $package 'Data'
        Require (-not (Test-Path -LiteralPath $defaultPortable)) 'Clean package already contains Data.'
        # Load only known, hash-verified UIA assemblies shipped with the native runtime.
        foreach ($assembly in @('WindowsBase.dll', 'UIAutomationTypes.dll', 'UIAutomationClient.dll')) {
            Add-Type -Path (Join-Path $package $assembly)
        }
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $package
        $start.ArgumentList.Add('--vault')
        $start.ArgumentList.Add($vault)
        $start.Environment['LOCALAPPDATA'] = $localAppData
        $start.Environment['DOTNET_ROOT'] = $missingRuntime
        $start.Environment['DOTNET_ROOT_X64'] = $missingRuntime
        $start.Environment['DOTNET_ROOT(x86)'] = $missingRuntime
        $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.Environment['PATH'] = (Join-Path $env:SystemRoot 'System32') + ';' + $env:SystemRoot
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $owned = [Diagnostics.Process]::Start($start)
        Require ($null -ne $owned) 'Process start returned null.'
        $report.processId = $owned.Id
        $ready = $false
        while ($timer.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
            Require (-not $owned.HasExited) "Relay exited before readiness (PID $($owned.Id))."
            $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $owned.Id)
            $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
            foreach ($window in $windows) {
                try {
                    $captureCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'main.capture')
                    $capture = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $captureCondition)
                    if ($null -ne $capture -and $capture.Current.ProcessId -eq $owned.Id -and $capture.Current.IsEnabled -and -not $capture.Current.IsOffscreen) {
                        $ready = $true; break
                    }
                } catch [System.Windows.Automation.ElementNotAvailableException] { }
            }
            if ($ready) { break }
            Start-Sleep -Milliseconds 100 # Bounded UI readiness wait, not a build/job poll.
        }
        Require $ready 'Timed out waiting for owned main.capture UIAutomation control.'
        $owned.Refresh()
        $core = @($owned.Modules | Where-Object { $_.ModuleName -eq 'coreclr.dll' })
        Require ($core.Count -eq 1 -and [string]::Equals($core[0].FileName, (Join-Path $package 'coreclr.dll'), [StringComparison]::OrdinalIgnoreCase)) 'App did not load coreclr from its own package.'
        Require (Test-Path -LiteralPath $vault -PathType Container) 'Explicit synthetic vault was not created.'
        Require (-not (Test-Path -LiteralPath $defaultPortable)) 'Launch unexpectedly created portable default Data.'
        Require (-not (Test-Path -LiteralPath (Join-Path $localAppData 'Relay/Vault'))) 'Launch unexpectedly created redirected default vault.'
        Pass 'isolated-uia-startup' ([ordered]@{ automationId = 'main.capture'; startupMs = $timer.ElapsedMilliseconds;
            workingSetBytes = $owned.WorkingSet64; packageCoreclrLoaded = $true; defaultDataCreated = $false })
        $report.launch = 'passed'
        $report.status = 'passed'
    }
    $exitCode = 0
} catch {
    $report.error = $_.Exception.Message
    $report.status = 'failed'
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
} finally {
    if ($null -ne $owned) {
        try {
            # Only the Process object created above, not every Relay or any process tree.
            if (-not $owned.HasExited) { $owned.Kill(); Require ($owned.WaitForExit(5000)) 'Owned smoke process did not terminate.' }
            $report.ownedProcessStopped = $true
        } catch {
            $report.ownedProcessStopped = $false; $report.status = 'failed'; $report.error = $_.Exception.Message; $exitCode = 1
            Write-Error -Message $_.Exception.Message -ErrorAction Continue
        } finally { $owned.Dispose() }
    }
    $report.finishedUtc = [datetime]::UtcNow.ToString('o')
    if (Test-Path -LiteralPath $runDir -PathType Container) {
        $null = Assert-WorkspacePath $reportPath
        $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
        Write-Host "Release test: $($report.status). Report: $reportPath"
    }
}
exit $exitCode
