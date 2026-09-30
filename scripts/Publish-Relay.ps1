#requires -Version 7.2
<# Publishes a self-contained native Windows folder. Default: portable mode.
No output directory or Data is deleted. ZIP uses only a fresh staging payload. #>
[CmdletBinding()]
param([switch]$Portable = $true, [string]$RuntimeLicenseRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$dist = Join-Path $root 'dist'
$output = [IO.Path]::GetFullPath((Join-Path $dist 'windows'))
$expectedOutput = [IO.Path]::GetFullPath((Join-Path $root 'dist/windows'))

function Assert-WorkspacePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside workspace: $full"
    }
    # Validate existing ancestors as well as the leaf. Refuse junctions/symlinks.
    $cursor = $full
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing reparse point: $cursor"
            }
        }
        if ($cursor -eq $root) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
        if (-not $cursor) { throw 'Workspace boundary validation failed.' }
    }
    return $full
}
function Assert-Output {
    if (-not [string]::Equals($output, $expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Output must be exactly <workspace>/dist/windows.'
    }
    $null = Assert-WorkspacePath $output
}
function Assert-NotRunning {
    # Conservative: never terminate a user's app, even one using a different vault.
    if (@(Get-Process -Name Relay -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Exit all Relay instances, including tray instances, before publishing. No process was stopped.'
    }
}
function Test-Excluded([string]$Relative) {
    return $Relative -match '(?i)(^|/)(Data|[.]git|tests?|artifacts|diagnostics?|node_modules|localStorage)(/|$)' -or
        $Relative -match '(?i)([.]pdb|[.]log|[.]dmp|[.]etl|[.]png|[.]jpe?g|[.]html?|[.]m?js|[.]css|[.]map)$' -or
        $Relative -match '(?i)(^|/)(createdump[.]exe|vault[.]json.*|focus[.]json.*|[.]relay[.]lock|.*(?:testhost|WebView2|msedge|chromium|playwright).*)$'
}

try {
    if (-not $IsWindows) { throw 'Publishing requires Windows and PowerShell 7.2+.' }
    Assert-Output
    Assert-NotRunning
    $project = Join-Path $root 'native/Relay.Desktop'
    foreach ($source in @('native/Relay.Desktop/Relay.Desktop.csproj', 'docs/Relay-Windows-Guide.md', 'docs/Relay-Quickstart.txt', 'LICENSE', 'NOTICE')) {
        $sourcePath = Assert-WorkspacePath (Join-Path $root $source)
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Required release input missing: $source" }
    }
    if (-not $Portable -and (Test-Path -LiteralPath (Join-Path $output 'portable.txt'))) {
        throw 'Existing output is portable. Refusing to change its data mode; use a separate clean workspace for a desktop package.'
    }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $sdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10[.]') { throw '.NET 10 SDK is required to build (not to run).' }
    $logDir = Assert-WorkspacePath (Join-Path $root "artifacts/release/$runId")
    $stage = Assert-WorkspacePath (Join-Path $dist "staging/$runId")
    $raw = Join-Path $stage 'publish'
    $payload = Join-Path $stage 'payload'
    $null = New-Item -ItemType Directory -Path $logDir, $raw, $payload -Force
    # Equivalent final output: dotnet publish native/Relay.Desktop -c Release -r win-x64
    # --self-contained true -p:PublishSingleFile=false -o dist/windows
    # Publish to fresh staging first so failed builds leave the working output alone.
    $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $raw)
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $log = Join-Path $logDir "publish-$attempt.log"
        & $dotnet @publishArgs 2>&1 | Tee-Object -FilePath $log | Out-Host
        $code = $LASTEXITCODE
        if ($code -eq 0) { break }
        $text = Get-Content -LiteralPath $log -Raw
        $authFailure = $text -match '(?i)(401|403|unauthorized|forbidden|authentication|credential)'
        $transient = $text -match '(?i)(NU1301|NU1300|NU1801|timed? out|temporar(?:y|ily)|connection reset|429|50[234])'
        if ($attempt -eq 3 -or $authFailure -or -not $transient) { throw "dotnet publish failed (exit $code); see $log" }
        Start-Sleep -Seconds (@(2, 5)[$attempt - 1])
    }
    foreach ($file in Get-ChildItem -LiteralPath $raw -Recurse -File -Force) {
        $null = Assert-WorkspacePath $file.FullName
        $relative = [IO.Path]::GetRelativePath($raw, $file.FullName).Replace([char]92, [char]47)
        if (Test-Excluded $relative) { continue }
        $target = Assert-WorkspacePath (Join-Path $payload $relative)
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs/Relay-Windows-Guide.md') -Destination (Join-Path $payload '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $root 'docs/Relay-Quickstart.txt') -Destination (Join-Path $payload '开始使用.txt')
    foreach ($name in @('LICENSE', 'NOTICE')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $payload $name) }
    if ($Portable) { Set-Content -LiteralPath (Join-Path $payload 'portable.txt') -Value 'Relay portable mode: Data beside Relay.exe. Preserve Data when upgrading.' -Encoding utf8 }
    foreach ($name in @('Relay.exe', 'Relay.dll', 'Relay.runtimeconfig.json', 'hostpolicy.dll', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "Incomplete self-contained output: $name" }
    }
    # Runtime legal inputs are read-only from the exact runtime-pack version, never downloaded.
    # Optional root has the same package-id/version layout as a NuGet global cache.
    $packageRoot = if ($RuntimeLicenseRoot) { [IO.Path]::GetFullPath($RuntimeLicenseRoot) }
        elseif ($env:NUGET_PACKAGES) { [IO.Path]::GetFullPath($env:NUGET_PACKAGES) }
        else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'Relay.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeLegal = [Collections.Generic.List[object]]::new()
    foreach ($frameworkName in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq $frameworkName })
        if ($framework.Count -ne 1 -or $framework[0].version -notmatch '^10[.][0-9]+[.][0-9]+(?:-[a-zA-Z0-9.-]+)?$') {
            throw "Invalid included runtime framework/version: $frameworkName"
        }
        $version = $framework[0].version
        $packageId = $frameworkName.ToLowerInvariant() + '.runtime.win-x64'
        $packDir = Join-Path $packageRoot "$packageId/$version"
        $licenseNames = if ($frameworkName -eq 'Microsoft.NETCore.App') { @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') } else { @('LICENSE') }
        if ($frameworkName -eq 'Microsoft.WindowsDesktop.App') {
            # Current WindowsDesktop packs contain LICENSE only; preserve notices if a future pack adds them.
            $licenseNames += @(Get-ChildItem -LiteralPath $packDir -File -ErrorAction Stop | Where-Object { $_.Name -match '(?i)third.*party.*notice' } | Select-Object -ExpandProperty Name)
        }
        foreach ($legalName in $licenseNames) {
            $source = Join-Path $packDir $legalName
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
                throw "Runtime legal file missing: $source. Supply -RuntimeLicenseRoot with matching package-id/version folders."
            }
            $relative = "licenses/$frameworkName/$version/$legalName"
            $destination = Assert-WorkspacePath (Join-Path $payload $relative)
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
            Copy-Item -LiteralPath $source -Destination $destination
            $runtimeLegal.Add([ordered]@{ framework = $frameworkName; version = $version; file = $relative })
        }
    }
    $entries = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ name = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace([char]92, [char]47); size = $_.Length;
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{ schemaVersion = 1; application = 'Relay'; version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'Relay.exe')).ProductVersion;
        createdUtc = [datetime]::UtcNow.ToString('o'); runtime = 'win-x64'; framework = 'net10.0-windows'; selfContained = $true;
        singleFile = $false; portable = [bool]$Portable; signingStatus = (Get-AuthenticodeSignature -LiteralPath (Join-Path $payload 'Relay.exe')).Status.ToString();
        runtimeLegal = @($runtimeLegal.ToArray()); files = $entries; note = 'Manifest excludes itself to avoid a recursive hash. No user data included.' }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $payload 'release-manifest.json') -Encoding utf8
    $stageZip = Assert-WorkspacePath (Join-Path $stage 'Relay-Windows-x64.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $stageZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    # Validate every destination BEFORE copying. Never delete/move output or enumerate Data.
    Assert-Output
    Assert-NotRunning
    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
    foreach ($file in $payloadFiles) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace([char]92, [char]47)
        if ($relative -match '(?i)^Data(/|$)') { throw 'Refusing to overwrite a vault.' }
        $target = Assert-WorkspacePath (Join-Path $output $relative)
        if (Test-Path -LiteralPath $target -PathType Container) { throw "File target is a directory: $target" }
    }
    $zip = Assert-WorkspacePath (Join-Path $dist 'Relay-Windows-x64.zip')
    $hashPath = Assert-WorkspacePath (Join-Path $dist 'Relay-Windows-x64.zip.sha256')
    $manifestPath = Assert-WorkspacePath (Join-Path $dist 'Relay-Windows-x64.manifest.json')
    $null = New-Item -ItemType Directory -Path $output -Force
    foreach ($file in $payloadFiles) {
        $target = Assert-WorkspacePath (Join-Path $output ([IO.Path]::GetRelativePath($payload, $file.FullName)))
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    Copy-Item -LiteralPath $stageZip -Destination $zip -Force
    Copy-Item -LiteralPath (Join-Path $payload 'release-manifest.json') -Destination $manifestPath -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $hashPath -Value "$hash  Relay-Windows-x64.zip" -Encoding ascii
    Write-Host "Published: $output"
    Write-Host "ZIP: $zip"
    Write-Host "SHA256: $hash"
    Write-Host "Portable: $([bool]$Portable); signature: $($manifest.signingStatus)"
    Write-Host "Logs/staging retained: $logDir ; $stage"
    Write-Host 'Existing Data and unrelated files were not deleted. Distribute the clean ZIP, not a used output folder.'
    exit 0
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
}
